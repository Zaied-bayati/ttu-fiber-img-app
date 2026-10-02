using System.Net.Http;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;
using ZenApi.Acquisition.V1Beta;

namespace Ttu.FiberImgApp.Integrations.Zen;

/// <summary>
/// Axiocam / ZEN live + still capture via the ZEN API Gateway (gRPC over TLS).
///
/// Live view follows Zeiss's zenapi_streaming.py / zenapi_stream2omezarr.py: ZEN only streams pixels for a
/// running experiment, so the pixel stream (MonitorAllExperiments) is opened first and the experiment is then
/// started with ExperimentService.StartExperiment. Frames are shown from the stream when ZEN sends them. If it
/// stays silent, each run's saved CZI is read instead (Zeiss's own examples also read the CZI after a run), so
/// a live image appears either way. StartLive/StartContinuous are not used: nothing in Zeiss's samples streams
/// pixels from them.
///
/// Stills (Begin Sampling) use RunSnap and read the CZI ZEN writes for the preview, PNG, TIFF and BMP.
/// </summary>
public sealed class ZenApiCameraService : ICameraService, IZenClient, IAsyncDisposable
{
    private const string LivePreviewPrefix = "LivePreview_";
    private const string ProbePrefix = "StreamProbe_";

    private readonly ZenOptions _options;
    private readonly ILogger<ZenApiCameraService> _logger;
    private readonly IActivityLog _activityLog;
    private readonly IReadOnlyList<ICziTileDecoder> _tileDecoders;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _snapLock = new(1, 1);
    private readonly ZenLiveFrameDecoder _decoder = new();

    private GrpcChannel? _channel;
    private ExperimentService.ExperimentServiceClient? _experiment;
    private ExperimentStreamingService.ExperimentStreamingServiceClient? _streaming;
    private Metadata? _metadata;
    private string? _experimentId;
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;
    private CameraFrame? _lastFrame;
    private string _status = "ZEN camera - disconnected";
    private bool _connected;
    private bool _live;
    private volatile bool _frameTooLarge;
    private volatile bool _waitingForZenUi;
    private int _liveFrames;
    private int _streamMessages;
    private int _streamFrames;
    private string _lastSkip = string.Empty;

    public ZenApiCameraService(
        IOptions<ZenOptions> options,
        ILogger<ZenApiCameraService> logger,
        IActivityLog activityLog,
        IEnumerable<ICziTileDecoder> tileDecoders)
    {
        _options = options.Value;
        _logger = logger;
        _activityLog = activityLog;
        _tileDecoders = tileDecoders.ToList();
    }

    public string Status { get { lock (_gate) return _status; } }
    public bool IsConnected { get { lock (_gate) return _connected; } }
    public bool IsLive { get { lock (_gate) return _live; } }

    public event EventHandler<CameraFrameEventArgs>? FrameReceived;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = ZenChannelFactory.ResolveToken(_options);
            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus("ZEN: missing control token. Paste it in Settings, or leave the token blank to use C:\\ProgramData\\Carl Zeiss\\ZEN APIGateway\\GlobalControlToken.txt.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_options.ExperimentName))
            {
                SetStatus("ZEN: set ExperimentName (Axiocam .czexp without extension)");
                return false;
            }

            await StopLiveAsync(cancellationToken).ConfigureAwait(false);
            await DisposeChannelAsync().ConfigureAwait(false);

            Exception? lastTransport = null;
            foreach (var port in ZenChannelFactory.CandidatePorts(_options.Port))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DisposeChannelAsync().ConfigureAwait(false);
                    _channel = ZenChannelFactory.Create(_options, port, out var transportNote);
                    _metadata = new Metadata { { "control-token", token } };
                    _experiment = new ExperimentService.ExperimentServiceClient(_channel);
                    _streaming = new ExperimentStreamingService.ExperimentStreamingServiceClient(_channel);

                    var load = await _experiment.LoadAsync(
                        new ExperimentServiceLoadRequest { ExperimentName = _options.ExperimentName },
                        headers: _metadata,
                        deadline: DateTime.UtcNow.AddSeconds(8),
                        cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

                    lock (_gate)
                    {
                        _experimentId = load.ExperimentId;
                        _connected = true;
                    }

                    await TryStopExperimentAsync(_experiment, _metadata, load.ExperimentId, cancellationToken)
                        .ConfigureAwait(false);

                    var portNote = port == _options.Port
                        ? $"port {port}"
                        : $"port {port} (saved port {_options.Port} did not answer)";
                    SetStatus($"ZEN connected - experiment '{_options.ExperimentName}' ({_options.Host}, {portNote}, {transportNote})");
                    return true;
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    lastTransport = ex;
                    _logger.LogWarning(ex, "ZEN gateway {Host}:{Port} did not answer", _options.Host, port);
                    await DisposeChannelAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ZEN Connect failed after the gateway answered");
                    SetStatus($"ZEN connect failed: {ex.Message}");
                    await DisposeChannelAsync().ConfigureAwait(false);
                    return false;
                }
            }

            var detail = lastTransport?.Message ?? "no gateway response";
            SetStatus(
                $"ZEN connect failed: gateway not reachable ({detail}). " +
                "Confirm ZEN API Gateway is running and the tray icon port matches Settings (default 5002).");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZEN Connect failed");
            SetStatus($"ZEN connect failed: {ex.Message}");
            await DisposeChannelAsync().ConfigureAwait(false);
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopLiveAsync(cancellationToken).ConfigureAwait(false);
        await DisposeChannelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _connected = false;
            _experimentId = null;
        }
        SetStatus("ZEN camera - disconnected");
    }

    // ---------------------------------------------------------------- live view

    public async Task StartLiveAsync(CancellationToken cancellationToken = default)
    {
        string experimentId;
        ExperimentService.ExperimentServiceClient experiment;
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming;
        Metadata metadata;

        lock (_gate)
        {
            if (!_connected || _experiment is null || _streaming is null || _metadata is null || _experimentId is null)
                throw new InvalidOperationException("Connect to ZEN before starting live.");
            if (_live && _liveTask is { IsCompleted: false })
                return;
            experimentId = _experimentId;
            experiment = _experiment;
            streaming = _streaming;
            metadata = _metadata;
        }

        if (_live || _liveTask is not null)
            await StopLiveAsync(cancellationToken).ConfigureAwait(false);

        await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        Task task;
        lock (_gate)
        {
            _liveCts = cts;
            _live = true;
            _frameTooLarge = false;
            _waitingForZenUi = false;
            _liveFrames = 0;
            _streamMessages = 0;
            _streamFrames = 0;
            _lastSkip = string.Empty;
            _decoder.Reset();
            task = Task.Run(() => LiveLoopAsync(experiment, streaming, metadata, experimentId, cts.Token), CancellationToken.None);
            _liveTask = task;
        }

        SetStatus("ZEN live starting: opening the pixel stream and starting the experiment...");

        try
        {
            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < 60_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _liveFrames) > 0 || _waitingForZenUi)
                    return;
                if (task.IsCompleted)
                    break;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _liveFrames) > 0)
                return;

            var message = task.IsCompleted ? Status : BuildNoFrameMessage(null);
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus(message);
        }
        catch (OperationCanceledException)
        {
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task LiveLoopAsync(
        ExperimentService.ExperimentServiceClient experiment,
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        CancellationToken token)
    {
        string? outputDirectory = null;
        var leftovers = new List<string>();
        try
        {
            // Open the pixel stream FIRST, before the experiment starts, so no early frames are missed
            // (zenapi_stream2omezarr.py). Without a channel filter ZEN returns every channel.
            using var monitor = streaming.MonitorAllExperiments(
                new ExperimentStreamingServiceMonitorAllExperimentsRequest
                {
                    ChannelIndex = _options.EffectiveChannelIndex,
                    EnableRawData = false,
                },
                headers: metadata,
                cancellationToken: token);
            var reader = Task.Run(() => ReadLiveStreamAsync(monitor.ResponseStream, token), CancellationToken.None);

            try
            {
                outputDirectory = await GetImageOutputDirectoryAsync(experiment, metadata, token).ConfigureAwait(false);
                CleanupPreviewFiles(outputDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ZEN image output folder unavailable; live view can only use the pixel stream");
            }

            var useSnap = false;
            var failures = 0;
            string? lastError = null;
            string? shownMode = null;

            while (!token.IsCancellationRequested)
            {
                var name = LivePreviewPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var streamFramesBefore = Volatile.Read(ref _streamFrames);
                string? iterationError = null;

                try
                {
                    if (useSnap)
                    {
                        await RunSnapOnceAsync(experiment, metadata, experimentId, name, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await StartExperimentOnceAsync(experiment, metadata, experimentId, name, token).ConfigureAwait(false);
                        await WaitForExperimentEndAsync(
                            experiment,
                            metadata,
                            experimentId,
                            outputDirectory is null ? null : Path.Combine(outputDirectory, name + ".czi"),
                            token).ConfigureAwait(false);
                    }
                }
                catch (RpcException ex) when (IsControllingDenied(ex))
                {
                    // Same as the sample's start_experiment_from_UI mode: only watch the stream.
                    _logger.LogWarning(ex, "ZEN refused to start the experiment");
                    _waitingForZenUi = true;
                    SetStatus(
                        "ZEN API control is locked (Unsupervised API Mode is off). Start the experiment in the ZEN window; " +
                        "this app will show its pixel stream. Turn on Tools > Options > ZEN API > Unsupervised API Mode to start it from here.");
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !(ex is RpcException { StatusCode: StatusCode.Cancelled }))
                {
                    iterationError = ex is RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : ex.Message;
                    lastError = iterationError;
                    _logger.LogWarning(ex, "ZEN live iteration failed");
                }

                var gotFrame = Volatile.Read(ref _streamFrames) > streamFramesBefore;
                CameraFrame? cziFrame = null;
                if (!gotFrame && outputDirectory is not null && iterationError is null)
                {
                    var read = await ReadCziWithRetryAsync(Path.Combine(outputDirectory, name + ".czi"), token, timeoutMs: 30_000).ConfigureAwait(false);
                    cziFrame = read.Frame;
                    if (cziFrame is null)
                        lastError = read.Error;
                }

                if (outputDirectory is not null)
                {
                    leftovers.Add(Path.Combine(outputDirectory, name + ".czi"));
                    leftovers.RemoveAll(TryDeleteQuiet);
                }

                if (gotFrame || cziFrame is not null)
                {
                    failures = 0;
                    lastError = null;
                    var mode = cziFrame is not null
                        ? "ZEN live running (reading each acquired image from its CZI, about one image every few seconds)"
                        : "ZEN live running (pixel stream)";
                    if (mode != shownMode)
                    {
                        shownMode = mode;
                        SetStatus(mode);
                    }

                    if (cziFrame is not null)
                        PublishFrame(cziFrame);
                    continue;
                }

                failures++;
                if (failures >= 2 && !useSnap)
                {
                    useSnap = true;
                    failures = 0;
                    SetStatus("StartExperiment produced no image; trying RunSnap for live view instead...");
                }
                else if (failures >= 3)
                {
                    SetStatus(BuildNoFrameMessage(lastError));
                    return;
                }
            }

            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop requested.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // Stop requested.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZEN live loop failed");
            SetStatus($"ZEN live failed: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                lock (_gate) _live = false;
            }

            foreach (var path in leftovers)
                TryDeleteQuiet(path);
        }
    }

    private async Task ReadLiveStreamAsync(
        IAsyncStreamReader<ExperimentStreamingServiceMonitorAllExperimentsResponse> reader,
        CancellationToken token)
    {
        try
        {
            await foreach (var response in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _streamMessages);
                var frame = _decoder.Add(response.FrameData, enableRawData: false, out var skip);
                if (frame is null)
                {
                    if (!string.IsNullOrEmpty(skip) && !skip.StartsWith("buffering", StringComparison.Ordinal))
                        _lastSkip = skip;
                    continue;
                }

                if (Interlocked.Increment(ref _streamFrames) == 1)
                {
                    SetStatus(
                        $"ZEN live running (pixel stream, {frame.Width}x{frame.Height} {frame.PixelFormat}, " +
                        $"channel {response.FrameData?.FramePosition?.C.ToString() ?? "n/a"})" +
                        (string.IsNullOrEmpty(skip) ? string.Empty : $" - {skip}"));
                }

                PublishFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Live stopped.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // Live stopped.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            _frameTooLarge = true;
            _logger.LogError(ex, "ZEN frame exceeds the receive limit");
        }
        catch (Exception ex)
        {
            _lastSkip = $"stream error: {ex.Message}";
            _logger.LogWarning(ex, "ZEN pixel stream ended");
        }
    }

    private void PublishFrame(CameraFrame frame)
    {
        lock (_gate) _lastFrame = frame;
        Interlocked.Increment(ref _liveFrames);
        try
        {
            FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Frame handler failed");
        }
    }

    private string BuildNoFrameMessage(string? lastError)
    {
        if (_frameTooLarge)
        {
            return "ZEN frame is larger than the gateway/client receive limit. Full Axiocam frames need a raised gRPC size (this app allows 256 MB).";
        }

        var stream = _streamMessages == 0
            ? "ZEN sent nothing on the pixel stream"
            : $"ZEN sent {_streamMessages} stream messages but none were displayable ({_lastSkip})";
        var detail = string.IsNullOrEmpty(lastError) ? string.Empty : $" Last problem: {lastError}.";
        return $"ZEN live could not get an image: {stream}.{detail} " +
               "Check that Unsupervised API Mode is on and that the experiment's active camera is the Axiocam.";
    }

    public async Task StopLiveAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? task;
        string? experimentId;
        ExperimentService.ExperimentServiceClient? experiment;
        Metadata? metadata;

        lock (_gate)
        {
            cts = _liveCts;
            task = _liveTask;
            experimentId = _experimentId;
            experiment = _experiment;
            metadata = _metadata;
            _liveCts = null;
            _liveTask = null;
            _live = false;
        }

        CancelQuiet(cts);
        try { if (task is not null) await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "ZEN live loop ended with an error"); }
        finally { DisposeQuiet(cts); }

        _decoder.Reset();

        if (experiment is not null && metadata is not null && !string.IsNullOrEmpty(experimentId))
        {
            try
            {
                await experiment.StopAsync(
                    new ExperimentServiceStopRequest { ExperimentId = experimentId },
                    headers: metadata,
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ZEN Stop after live failed (ignored if nothing was running)");
            }
        }

        if (IsConnected) SetStatus("ZEN connected - live stopped");
    }

    // ---------------------------------------------------------------- experiment calls (as in the Zeiss samples)

    private async Task StartExperimentOnceAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        string outputName,
        CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Does not wait for the experiment to finish.
                await experiment.StartExperimentAsync(
                    new ExperimentServiceStartExperimentRequest { ExperimentId = experimentId, OutputName = outputName },
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddSeconds(60),
                    cancellationToken: token).ResponseAsync.ConfigureAwait(false);
                return;
            }
            catch (RpcException ex) when (attempt == 0 && IsAlreadyActive(ex))
            {
                SetStatus("ZEN: experiment already active - stopping it and retrying...");
                await TryStopExperimentAsync(experiment, metadata, experimentId, token).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSnapOnceAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        string outputName,
        CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await experiment.RunSnapAsync(
                    new ExperimentServiceRunSnapRequest { ExperimentId = experimentId, OutputName = outputName },
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddMinutes(2),
                    cancellationToken: token).ResponseAsync.ConfigureAwait(false);
                return;
            }
            catch (RpcException ex) when (attempt == 0 && IsAlreadyActive(ex))
            {
                await TryStopExperimentAsync(experiment, metadata, experimentId, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Polls until the experiment has run and stopped. A just-started experiment may not report "running" yet, so a
    /// "not running" answer only ends the wait once it was seen running, its CZI already exists, or a few seconds passed.
    /// </summary>
    private async Task WaitForExperimentEndAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        string? expectedCziPath,
        CancellationToken token)
    {
        var started = Environment.TickCount64;
        var errors = 0;
        var sawRunning = false;
        await Task.Delay(300, token).ConfigureAwait(false);
        while (Environment.TickCount64 - started < 600_000)
        {
            try
            {
                var response = await experiment.GetStatusAsync(
                    new ExperimentServiceGetStatusRequest { ExperimentId = experimentId },
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddSeconds(8),
                    cancellationToken: token).ResponseAsync.ConfigureAwait(false);
                if (response.Status is not null && response.Status.IsExperimentRunning)
                {
                    sawRunning = true;
                }
                else if (sawRunning
                         || (expectedCziPath is not null && File.Exists(expectedCziPath))
                         || Environment.TickCount64 - started > 3_000)
                {
                    return;
                }
            }
            catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
            {
                if (++errors >= 3)
                    return;
            }

            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- probe

    public async Task<ZenStreamProbeResult> ProbeStreamAsync(TimeSpan listenFor, CancellationToken cancellationToken = default)
    {
        ExperimentService.ExperimentServiceClient experiment;
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming;
        Metadata metadata;
        string experimentId;

        lock (_gate)
        {
            if (!_connected || _experiment is null || _streaming is null || _metadata is null || _experimentId is null)
                throw new InvalidOperationException("Connect to ZEN before detecting the stream channel.");
            experiment = _experiment;
            streaming = _streaming;
            metadata = _metadata;
            experimentId = _experimentId;
        }

        await _snapLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedExperiment = false;
        try
        {
            if (IsLive)
                await StopLiveAsync(cancellationToken).ConfigureAwait(false);
            await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

            var collector = new ZenStreamProbeCollector();
            SetStatus("ZEN probing the pixel stream (no channel filter, experiment started like the Zeiss sample)...");

            string? outputDirectory = null;
            try { outputDirectory = await GetImageOutputDirectoryAsync(experiment, metadata, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { collector.AddNote($"Image output folder unavailable: {ex.Message}"); }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(listenFor);

            // Streams first, then the experiment, exactly as zenapi_stream2omezarr.py does.
            using var allExperiments = streaming.MonitorAllExperiments(
                new ExperimentStreamingServiceMonitorAllExperimentsRequest { EnableRawData = false },
                headers: metadata,
                cancellationToken: window.Token);
            using var thisExperiment = streaming.MonitorExperiment(
                new ExperimentStreamingServiceMonitorExperimentRequest { ExperimentId = experimentId, EnableRawData = false },
                headers: metadata,
                cancellationToken: window.Token);
            var readers = Task.WhenAll(
                ProbeReadAsync("MonitorAllExperiments", allExperiments.ResponseStream, r => r.FrameData, collector, window.Token),
                ProbeReadAsync("MonitorExperiment", thisExperiment.ResponseStream, r => r.FrameData, collector, window.Token));

            var name = ProbePrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            try
            {
                await StartExperimentOnceAsync(experiment, metadata, experimentId, name, window.Token).ConfigureAwait(false);
                startedExperiment = true;
                collector.AddNote($"Started the experiment with StartExperiment (output {name}).");
            }
            catch (RpcException ex) when (IsControllingDenied(ex))
            {
                collector.AddNote("ZEN refused StartExperiment (Unsupervised API Mode is off), so only an experiment started in the ZEN window could be heard.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                collector.AddNote($"StartExperiment failed: {ex.Message}");
            }

            await readers.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var czi = outputDirectory is null ? null : Path.Combine(outputDirectory, name + ".czi");
            var cziWritten = czi is not null && File.Exists(czi);
            if (cziWritten)
            {
                var read = await ReadCziWithRetryAsync(czi!, CancellationToken.None).ConfigureAwait(false);
                collector.AddNote(read.Frame is not null
                    ? $"ZEN saved {name}.czi and this app read it as {read.Frame.Width}x{read.Frame.Height} {read.Frame.PixelFormat}."
                    : $"ZEN saved {name}.czi but this app could not read it: {read.Error}");
                TryDeleteQuiet(czi!);
            }

            var result = collector.Build(listenFor, cziWritten);
            SetStatus(result.PixelMessages > 0
                ? $"ZEN probe: pixels arrive on channel(s) {string.Join(", ", result.Channels)}"
                : cziWritten
                    ? "ZEN probe: no pixels were streamed, but the CZI was read (live view and previews use the CZI)"
                    : "ZEN probe: no pixels arrived (see Settings for details)");
            _activityLog.Write("zen", result.Summary);
            return result;
        }
        finally
        {
            if (startedExperiment)
                await TryStopExperimentAsync(experiment, metadata, experimentId, CancellationToken.None).ConfigureAwait(false);
            _snapLock.Release();
        }
    }

    private async Task ProbeReadAsync<TResponse>(
        string source,
        IAsyncStreamReader<TResponse> reader,
        Func<TResponse, FrameData?> frameOf,
        ZenStreamProbeCollector collector,
        CancellationToken token)
    {
        try
        {
            await foreach (var response in reader.ReadAllAsync(token).ConfigureAwait(false))
                collector.Add(source, frameOf(response));
        }
        catch (OperationCanceledException)
        {
            // The listen window ended.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // The listen window ended.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            collector.AddNote($"{source}: a frame was larger than the gRPC receive limit.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ZEN probe listener {Source} failed", source);
            collector.AddNote($"{source}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- stills (Begin Sampling)

    public async Task<byte[]> CaptureStillAsync(CancellationToken cancellationToken = default)
    {
        var capture = await CaptureAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        return capture.PreviewPng;
    }

    public async Task<CameraCapture> CaptureAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        await _snapLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SnapAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _snapLock.Release();
        }
    }

    /// <summary>
    /// ExperimentService.RunSnap acquires one image with the experiment's activated channels and waits until ZEN
    /// finishes. ZEN writes the CZI; that file is the source of the preview PNG, 16-bit TIFF and 8-bit BMP. A
    /// pixel-stream listener runs alongside only as a fallback if the CZI cannot be read.
    /// </summary>
    private async Task<CameraCapture> SnapAsync(CancellationToken cancellationToken)
    {
        ExperimentService.ExperimentServiceClient experiment;
        ExperimentStreamingService.ExperimentStreamingServiceClient? streaming;
        Metadata metadata;
        string experimentId;

        lock (_gate)
        {
            if (!_connected || _experiment is null || _metadata is null || _experimentId is null)
                throw new InvalidOperationException("ZEN camera is not connected.");
            experiment = _experiment;
            streaming = _streaming;
            metadata = _metadata;
            experimentId = _experimentId;
        }

        if (IsLive)
            await StopLiveAsync(cancellationToken).ConfigureAwait(false);

        await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

        var outputDirectory = await GetImageOutputDirectoryAsync(experiment, metadata, cancellationToken).ConfigureAwait(false);
        var outputName = UniqueOutputName(outputDirectory);
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int? capturedChannel = null;
        string? lastSnapSkip = null;
        var frameTask = streaming is null
            ? Task.FromResult<CameraFrame?>(null)
            : ListenForSnapFrameAsync(streaming, metadata, experimentId, ready, value => capturedChannel = value, reason => lastSnapSkip = reason, listenCts.Token);

        try
        {
            if (streaming is not null)
            {
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "ZEN snap pixel stream did not open before RunSnap");
                }
            }

            ExperimentServiceRunSnapResponse snap;
            try
            {
                snap = await experiment.RunSnapAsync(
                    new ExperimentServiceRunSnapRequest
                    {
                        ExperimentId = experimentId,
                        OutputName = outputName,
                    },
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddMinutes(2),
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException ex) when (IsAlreadyActive(ex))
            {
                await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);
                snap = await experiment.RunSnapAsync(
                    new ExperimentServiceRunSnapRequest
                    {
                        ExperimentId = experimentId,
                        OutputName = outputName,
                    },
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddMinutes(2),
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }

            var writtenName = string.IsNullOrWhiteSpace(snap.OutputName) ? outputName : snap.OutputName.Trim();
            var cziPath = Path.Combine(outputDirectory, writtenName + ".czi");
            var appeared = Environment.TickCount64;
            while (!File.Exists(cziPath) && Environment.TickCount64 - appeared < 3000)
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(cziPath))
            {
                throw new FileNotFoundException(
                    $"ZEN RunSnap finished but did not write {cziPath}. Confirm the experiment's image output folder.",
                    cziPath);
            }

            // The CZI is the preview source: it does not depend on the pixel stream working.
            var read = await ReadCziWithRetryAsync(cziPath, cancellationToken).ConfigureAwait(false);
            var frame = read.Frame;
            var frameSource = "CZI file";

            if (frame is null)
            {
                _logger.LogWarning("Could not read {Czi} for the preview: {Error}", cziPath, read.Error);
                frameSource = "pixel stream";
                try
                {
                    frame = await frameTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("ZEN RunSnap wrote {Czi} but no pixel frame arrived for the preview", cziPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ZEN snap preview listener failed after {Czi} was written", cziPath);
                }
            }

            string? tiffPath = null;
            string? bmpPath = null;
            string? placeholderReason = null;
            byte[] preview;
            if (frame is null)
            {
                preview = PlaceholderPreview();
                var streamNote = string.IsNullOrEmpty(lastSnapSkip) ? "no pixel messages arrived from ZEN" : lastSnapSkip;
                placeholderReason = $"the CZI could not be read ({read.Error}) and {streamNote}";
                SetStatus($"ZEN snap saved {cziPath}. No preview image: {placeholderReason}.");
            }
            else
            {
                preview = MonoFrameEncoder.ToPng(frame);
                tiffPath = TryWriteTiff(outputDirectory, writtenName, frame);
                bmpPath = TryWriteBmp(outputDirectory, writtenName, frame);
                SetStatus(
                    $"Capture received from {frameSource}: experiment {experimentId}, {frame.Width}x{frame.Height} {frame.PixelFormat}, " +
                    $"raw {frame.RawPixels.Length} bytes, CZI {cziPath}");
                _logger.LogInformation(
                    "Capture received: Source={Source} ExperimentId={ExperimentId} Width={Width} Height={Height} PixelFormat={PixelFormat} Channel={Channel} RawBytes={RawBytes} Czi={Czi} Tiff={Tiff}",
                    frameSource,
                    experimentId,
                    frame.Width,
                    frame.Height,
                    frame.PixelFormat,
                    capturedChannel,
                    frame.RawPixels.Length,
                    cziPath,
                    tiffPath);
                lock (_gate) _lastFrame = frame;
                try { FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame)); }
                catch (Exception ex) { _logger.LogWarning(ex, "Snap preview handler failed"); }
            }

            return new CameraCapture
            {
                PreviewPng = preview,
                CziPath = cziPath,
                TiffPath = tiffPath,
                BmpPath = bmpPath,
                Width = frame?.Width ?? 0,
                Height = frame?.Height ?? 0,
                PixelFormat = frame?.PixelFormat.ToString(),
                Channel = capturedChannel,
                PlaceholderReason = placeholderReason,
            };
        }
        finally
        {
            listenCts.Cancel();
            try { await frameTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "ZEN snap listener ended"); }
        }
    }

    /// <summary>Reads the first plane of a CZI, waiting briefly for ZEN to finish writing it.</summary>
    private async Task<(CameraFrame? Frame, string Error)> ReadCziWithRetryAsync(string path, CancellationToken token, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var lastError = "the file was not found";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var result = await Task.Run(
                    () =>
                    {
                        var ok = CziReader.TryReadFirstPlane(path, _tileDecoders, out var frame, out var error, out var permanent);
                        return (Frame: ok ? frame : null, Error: error, Permanent: permanent);
                    },
                    CancellationToken.None).ConfigureAwait(false);

                if (result.Frame is not null)
                    return (result.Frame, string.Empty);

                lastError = result.Error;
                if (result.Permanent)
                    return (null, lastError);
            }

            if (Environment.TickCount64 >= deadline)
                return (null, lastError);
            await Task.Delay(300, token).ConfigureAwait(false);
        }
    }

    private async Task<string> GetImageOutputDirectoryAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        CancellationToken cancellationToken)
    {
        var response = await experiment.GetImageOutputPathAsync(
            new ExperimentServiceGetImageOutputPathRequest(),
            headers: metadata,
            deadline: DateTime.UtcNow.AddSeconds(8),
            cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response.ImageOutputPath))
            throw new InvalidOperationException("ZEN did not report an image output folder.");

        Directory.CreateDirectory(response.ImageOutputPath);
        return response.ImageOutputPath;
    }

    private static string UniqueOutputName(string directory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var name = $"Capture_{stamp}";
        var suffix = 2;
        while (File.Exists(Path.Combine(directory, name + ".czi")) || File.Exists(Path.Combine(directory, name + ".tif")))
        {
            name = $"Capture_{stamp}_{suffix}";
            suffix++;
        }

        return name;
    }

    private async Task<CameraFrame?> ListenForSnapFrameAsync(
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        TaskCompletionSource ready,
        Action<int?> reportChannel,
        Action<string> reportSkip,
        CancellationToken token)
    {
        try
        {
            using var call = streaming.MonitorExperiment(
                new ExperimentStreamingServiceMonitorExperimentRequest
                {
                    ExperimentId = experimentId,
                    ChannelIndex = _options.EffectiveChannelIndex,
                    EnableRawData = false,
                },
                headers: metadata,
                cancellationToken: token);

            _decoder.Reset();
            ready.TrySetResult();
            await foreach (var response in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                reportChannel(response.FrameData?.FramePosition?.C);
                var frame = _decoder.Add(response.FrameData, enableRawData: false, out var skip);
                if (!string.IsNullOrEmpty(skip))
                    reportSkip(skip);

                if (frame is not null)
                {
                    if (!string.IsNullOrEmpty(skip))
                        _activityLog.Write("zen", $"Pixel-format warning on snap: {skip}");
                    return frame;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            return null;
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            _logger.LogWarning(ex, "ZEN snap pixel stream failed");
            return null;
        }
    }

    private string? TryWriteTiff(string directory, string outputName, CameraFrame frame)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Gray8 or CameraPixelFormat.Gray16))
        {
            _logger.LogInformation("Skipping TIFF for {Format}; the ZEN CZI remains the master file", frame.PixelFormat);
            return null;
        }

        var path = Path.Combine(directory, outputName + ".tif");
        if (File.Exists(path))
            path = Path.Combine(directory, outputName + "_" + Guid.NewGuid().ToString("N")[..8] + ".tif");

        try
        {
            MonoTiffWriter.Write(path, frame);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write TIFF {Path}", path);
            return null;
        }
    }

    private string? TryWriteBmp(string directory, string outputName, CameraFrame frame)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Gray8 or CameraPixelFormat.Gray16))
        {
            _logger.LogInformation("Skipping BMP for {Format}; the ZEN CZI remains the master file", frame.PixelFormat);
            return null;
        }

        var path = Path.Combine(directory, outputName + ".bmp");
        if (File.Exists(path))
            path = Path.Combine(directory, outputName + "_" + Guid.NewGuid().ToString("N")[..8] + ".bmp");

        try
        {
            MonoBmpWriter.Write(path, frame);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write BMP {Path}", path);
            return null;
        }
    }

    private static byte[] PlaceholderPreview()
    {
        var pixels = new byte[32 * 32];
        Array.Fill(pixels, (byte)32);
        return MonoFrameEncoder.ToPng(new CameraFrame
        {
            RawPixels = pixels,
            Width = 32,
            Height = 32,
            PixelFormat = CameraPixelFormat.Gray8,
        });
    }

    public Task<byte[]> GetPreviewFrameAsync(CancellationToken cancellationToken = default)
    {
        CameraFrame? frame;
        lock (_gate) frame = _lastFrame;
        if (frame is null)
            throw new InvalidOperationException("No frame yet. Start live preview first.");
        return Task.FromResult(MonoFrameEncoder.ToPng(frame));
    }

    // ---------------------------------------------------------------- helpers

    private async Task TryStopExperimentAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await experiment.StopAsync(
                new ExperimentServiceStopRequest { ExperimentId = experimentId },
                headers: metadata,
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ZEN Stop before start (ignored if nothing was running)");
        }
    }

    /// <summary>Deletes this app's own throwaway preview/probe CZIs (never anything else) that are a couple of minutes old.</summary>
    private void CleanupPreviewFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            foreach (var prefix in new[] { LivePreviewPrefix, ProbePrefix })
            {
                foreach (var file in Directory.EnumerateFiles(directory, prefix + "*.czi"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        TryDeleteQuiet(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tidy old preview files");
        }
    }

    private static bool TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAlreadyActive(RpcException ex) =>
        ex.Status.Detail.Contains("already active", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already active", StringComparison.OrdinalIgnoreCase);

    private static bool IsControllingDenied(RpcException ex) =>
        ex.StatusCode == StatusCode.PermissionDenied
        || ex.Status.Detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
        || ex.Status.Detail.Contains("API mode", StringComparison.OrdinalIgnoreCase)
        || ex.Status.Detail.Contains("unsupervised", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or System.Security.Authentication.AuthenticationException)
                return true;
            if (current is RpcException rpc && rpc.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
                return true;
        }

        return false;
    }

    private static void CancelQuiet(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void DisposeQuiet(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
        _activityLog.Write("zen", status);
    }

    private async Task DisposeChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        _experiment = null;
        _streaming = null;
        _metadata = null;
        if (channel is null)
            return;

        try
        {
            await channel.ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            channel.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}
