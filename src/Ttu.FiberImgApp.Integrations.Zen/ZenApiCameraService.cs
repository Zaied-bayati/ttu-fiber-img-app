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
/// Live view works like Begin Sampling, without the pixel stream: it asks ZEN for a snap (RunSnap), and as soon
/// as ZEN has written the CZI it queues the file and asks for the next snap, while a second task reads each
/// queued CZI in order, shows it and deletes it (see <see cref="LiveSnapPipeline"/>). StartLive/StartContinuous
/// are not used.
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
    private int _liveFrames;
    private LiveSnapPipeline? _pipeline;
    private readonly LiveRateMeter _rate = new(10_000);
    private volatile string _liveMode = string.Empty;
    private long _rateStatusAt;
    private bool _rateLogged;

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
        Metadata metadata;

        lock (_gate)
        {
            if (!_connected || _experiment is null || _metadata is null || _experimentId is null)
                throw new InvalidOperationException("Connect to ZEN before starting live.");
            if (_live && _liveTask is { IsCompleted: false })
                return;
            experimentId = _experimentId;
            experiment = _experiment;
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
            _liveFrames = 0;
            _liveMode = string.Empty;
            _rateStatusAt = 0;
            _rateLogged = false;
            _rate.Reset();
            task = Task.Run(() => LiveRunAsync(experiment, metadata, experimentId, cts.Token), CancellationToken.None);
            _liveTask = task;
        }

        SetStatus("ZEN live starting: taking the first image...");

        try
        {
            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < 60_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _liveFrames) > 0)
                    return;
                if (task.IsCompleted)
                    break;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _liveFrames) > 0)
                return;

            var message = task.IsCompleted ? Status : BuildNoFrameMessage();
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus(message);
        }
        catch (OperationCanceledException)
        {
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs live view until stopped: <see cref="LiveSnapPipeline"/> keeps asking ZEN for snaps (RunSnap, as Begin
    /// Sampling does) while a second task reads each finished CZI, shows it and deletes it.
    /// </summary>
    private async Task LiveRunAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        CancellationToken token)
    {
        try
        {
            string outputDirectory;
            try
            {
                outputDirectory = await GetImageOutputDirectoryAsync(experiment, metadata, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not RpcException { StatusCode: StatusCode.Cancelled })
            {
                _logger.LogWarning(ex, "ZEN image output folder unavailable");
                SetStatus($"ZEN live needs ZEN's image output folder but could not get it: {ex.Message}");
                return;
            }

            CleanupPreviewFiles(outputDirectory);

            var pipeline = new LiveSnapPipeline(
                acquire: ct => AcquireLiveSnapAsync(experiment, metadata, experimentId, outputDirectory, ct),
                read: (path, ct) => ReadCziWithRetryAsync(path, ct, timeoutMs: 30_000),
                publish: PublishLiveFrame,
                delete: path => !IsOwnLivePreview(path, outputDirectory) || TryDeleteQuiet(path));
            Volatile.Write(ref _pipeline, pipeline);

            await pipeline.RunAsync(token).ConfigureAwait(false);

            if (pipeline.FailureReason is { } reason && !token.IsCancellationRequested)
                SetStatus(BuildLiveFailureMessage(reason));
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
            _logger.LogError(ex, "ZEN live failed");
            SetStatus($"ZEN live failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _pipeline, null);
            if (!token.IsCancellationRequested)
            {
                lock (_gate) _live = false;
            }
        }
    }

    /// <summary>One snap for live view: RunSnap returns once ZEN has finished, and the path of the CZI it wrote is handed back.</summary>
    private async Task<string> AcquireLiveSnapAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        string outputDirectory,
        CancellationToken token)
    {
        var name = LivePreviewPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        try
        {
            var written = await RunSnapOnceAsync(experiment, metadata, experimentId, name, token).ConfigureAwait(false);
            return Path.Combine(outputDirectory, written + ".czi");
        }
        catch (RpcException ex) when (IsControllingDenied(ex))
        {
            throw new LiveSnapFatalException(
                "ZEN API control is locked (Unsupervised API Mode is off). " +
                "Turn on Tools > Options > ZEN API > Unsupervised API Mode in ZEN, then start live again.");
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw new InvalidOperationException($"{ex.StatusCode}: {ex.Status.Detail}", ex);
        }
    }

    /// <summary>True only for this app's own throwaway live-view file inside ZEN's image folder, so nothing else is ever deleted.</summary>
    private static bool IsOwnLivePreview(string path, string outputDirectory)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            var expected = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var file = Path.GetFileName(path);
            return directory is not null
                && string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase)
                && file.StartsWith(LivePreviewPrefix, StringComparison.Ordinal)
                && file.EndsWith(".czi", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void PublishLiveFrame(CameraFrame frame)
    {
        if (_liveMode.Length == 0)
        {
            const string mode = "ZEN live running (snap -> read CZI)";
            _liveMode = mode;
            SetStatus(mode);
        }

        PublishFrame(frame);
    }

    private void PublishFrame(CameraFrame frame)
    {
        lock (_gate) _lastFrame = frame;
        var total = Interlocked.Increment(ref _liveFrames);
        try
        {
            FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Frame handler failed");
        }

        ReportLiveRate(total);
    }

    /// <summary>
    /// Shows the measured frame rate in the status line, and writes it to the activity log once per live session
    /// with where the time goes (the ZEN snap vs reading and showing the image) so the next tuning step is obvious.
    /// </summary>
    private void ReportLiveRate(int totalFrames)
    {
        var now = Environment.TickCount64;
        var fps = _rate.Add(now, out _);
        var mode = _liveMode;
        if (fps <= 0 || mode.Length == 0 || now - Interlocked.Read(ref _rateStatusAt) < 1_000)
            return;

        Interlocked.Exchange(ref _rateStatusAt, now);
        lock (_gate) _status = $"{mode} - {fps:0.0} frames/s";

        if (totalFrames >= 5 && !_rateLogged)
        {
            _rateLogged = true;
            var pipeline = Volatile.Read(ref _pipeline);
            var detail = pipeline is null
                ? string.Empty
                : $" (ZEN snap {pipeline.LastAcquireMs} ms, read+show {pipeline.LastReadMs} ms, {pipeline.QueueDepth} waiting)";
            _activityLog.Write("zen", $"Live view rate: {fps:0.0} frames/s{detail}");
        }
    }

    private static string BuildLiveFailureMessage(string reason) =>
        reason.Contains("Unsupervised API Mode", StringComparison.Ordinal)
            ? reason
            : $"ZEN live could not get an image: {reason}. " +
              "Check that Unsupervised API Mode is on and that the experiment's active camera is the Axiocam.";

    private static string BuildNoFrameMessage() =>
        "ZEN live could not get an image within 60 seconds. " +
        "Check that Unsupervised API Mode is on and that the experiment's active camera is the Axiocam.";

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

    /// <summary>RunSnap blocks until ZEN has finished the acquisition; returns the name ZEN wrote (the CZI is "name.czi").</summary>
    private async Task<string> RunSnapOnceAsync(
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
                var snap = await experiment.RunSnapAsync(
                    new ExperimentServiceRunSnapRequest { ExperimentId = experimentId, OutputName = outputName },
                    headers: metadata,
                    deadline: DateTime.UtcNow.AddMinutes(2),
                    cancellationToken: token).ResponseAsync.ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(snap.OutputName) ? outputName : snap.OutputName.Trim();
            }
            catch (RpcException ex) when (attempt == 0 && IsAlreadyActive(ex))
            {
                await TryStopExperimentAsync(experiment, metadata, experimentId, token).ConfigureAwait(false);
            }
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
            await Task.Delay(50, token).ConfigureAwait(false);
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
