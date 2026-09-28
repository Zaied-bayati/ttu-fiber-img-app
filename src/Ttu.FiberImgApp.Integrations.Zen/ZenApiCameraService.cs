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
/// Live pixels follow the ZEN API streaming sample: ZEN acquires, then
/// ExperimentStreamingService.MonitorExperiment (or MonitorAllExperiments when Live
/// was started in the ZEN window) pushes FrameData. This app reads PixelData.RawData
/// on a background task and keeps only the newest frame for the UI.
/// Stills are encoded from that latest frame (PNG).
/// </summary>
public sealed class ZenApiCameraService : ICameraService, IZenClient, IAsyncDisposable
{
    private readonly ZenOptions _options;
    private readonly ILogger<ZenApiCameraService> _logger;
    private readonly IActivityLog _activityLog;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _snapLock = new(1, 1);
    private readonly ZenLiveFrameDecoder _decoder = new();
    private int _attemptId;

    private GrpcChannel? _channel;
    private ExperimentService.ExperimentServiceClient? _experiment;
    private ExperimentStreamingService.ExperimentStreamingServiceClient? _streaming;
    private Metadata? _metadata;
    private string? _experimentId;
    private CancellationTokenSource? _liveCts;
    private CancellationTokenSource? _streamCts;
    private Task? _liveLoop;
    private CameraFrame? _lastFrame;
    private string _status = "ZEN camera - disconnected";
    private bool _connected;
    private bool _live;
    private volatile bool _frameTooLarge;
    private int _streamMessages;
    private string _lastSkip = string.Empty;

    public ZenApiCameraService(IOptions<ZenOptions> options, ILogger<ZenApiCameraService> logger, IActivityLog activityLog)
    {
        _options = options.Value;
        _logger = logger;
        _activityLog = activityLog;
    }

    public string Status { get { lock (_gate) return _status; } }
    public bool IsConnected { get { lock (_gate) return _connected; } }
    public bool IsLive { get { lock (_gate) return _live; } }
    public bool IsPreviewAvailable => true;

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
            if (_live && _liveLoop is { IsCompleted: false })
                return;
            experimentId = _experimentId;
            experiment = _experiment;
            streaming = _streaming;
            metadata = _metadata;
        }

        if (_live || _liveLoop is not null)
            await StopLiveAsync(cancellationToken).ConfigureAwait(false);

        await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _liveCts = cts;
            _live = true;
            _frameTooLarge = false;
            _streamMessages = 0;
            _lastSkip = string.Empty;
            _decoder.Reset();
        }

        // The pixel stream stays tied to this CTS until Stop. The read loop runs on the thread pool;
        // cancelling it here (when StartLiveAsync returns) would drop the stream after the first frame.
        try
        {
            var controlling = await TryStartAcquisitionAsync(experiment, metadata, experimentId, live: true, cancellationToken)
                .ConfigureAwait(false);

            if (await WatchForFramesAsync(streaming, metadata, experimentId, controlling, cts.Token, cancellationToken).ConfigureAwait(false))
            {
                SetStatus(controlling ? "ZEN live running" : "ZEN live running (stream from ZEN UI)");
                return;
            }

            if (_frameTooLarge)
                throw new InvalidOperationException(Status);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCancelled(cts))
                throw new OperationCanceledException();

            if (controlling)
            {
                SetStatus("ZEN StartLive produced no frames — stopping, then StartContinuous...");
                await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);
                _decoder.Reset();
                await TryStartAcquisitionAsync(experiment, metadata, experimentId, live: false, cancellationToken)
                    .ConfigureAwait(false);

                if (await WatchForFramesAsync(streaming, metadata, experimentId, controlling: true, cts.Token, cancellationToken).ConfigureAwait(false))
                {
                    SetStatus("ZEN live running (continuous)");
                    return;
                }
            }

            if (_frameTooLarge)
                throw new InvalidOperationException(Status);

            var noFrames = _streamMessages == 0
                ? "ZEN acquisition started but the Gateway sent 0 pixel messages. " +
                  "Leave Live running in ZEN (the image must be updating there), then press Start live again. " +
                  "Also confirm Unsupervised API Mode is on and this experiment's active track is the Axiocam."
                : $"ZEN sent {_streamMessages} stream messages but no displayable image ({_lastSkip}).";
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus(noFrames);
        }
        catch (OperationCanceledException)
        {
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus($"ZEN live failed: {ex.Message}");
            throw;
        }
    }

    public async Task StopLiveAsync(CancellationToken cancellationToken = default)
    {
        // Invalidate in-flight attempts so a late frame cannot be treated as a successful start.
        Interlocked.Increment(ref _attemptId);

        CancellationTokenSource? cts;
        CancellationTokenSource? streamCts;
        Task? loop;
        string? experimentId;
        ExperimentService.ExperimentServiceClient? experiment;
        Metadata? metadata;

        lock (_gate)
        {
            cts = _liveCts;
            streamCts = _streamCts;
            loop = _liveLoop;
            experimentId = _experimentId;
            experiment = _experiment;
            metadata = _metadata;
            _liveCts = null;
            _streamCts = null;
            _liveLoop = null;
            _live = false;
        }

        CancelQuiet(streamCts);
        CancelQuiet(cts);
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            DisposeQuiet(streamCts);
            DisposeQuiet(cts);
        }

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
                _logger.LogWarning(ex, "ZEN Stop after live failed");
            }
        }

        if (IsConnected) SetStatus("ZEN connected - live stopped");
    }

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
    /// ExperimentService.RunSnap acquires one image with the experiment's activated channels
    /// and waits until ZEN finishes. ZEN writes the CZI. A short monitor collects the same
    /// frame for the preview and a 16-bit TIFF when pixels arrive.
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
        var frameTask = streaming is null
            ? Task.FromResult<CameraFrame?>(null)
            : ListenForSnapFrameAsync(streaming, metadata, experimentId, ready, value => capturedChannel = value, listenCts.Token);

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

            CameraFrame? frame = null;
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

            string? tiffPath = null;
            byte[] preview;
            if (frame is null)
            {
                preview = PlaceholderPreview();
                SetStatus($"ZEN snap saved {cziPath}. No preview frame was received.");
            }
            else
            {
                preview = MonoFrameEncoder.ToPng(frame);
                tiffPath = TryWriteTiff(outputDirectory, writtenName, frame);
                SetStatus(
                    $"Capture received: experiment {experimentId}, {frame.Width}x{frame.Height} {frame.PixelFormat}, " +
                    $"channel {capturedChannel?.ToString() ?? "n/a"}, raw {frame.RawPixels.Length} bytes, CZI {cziPath}");
                _logger.LogInformation(
                    "Capture received: ExperimentId={ExperimentId} Width={Width} Height={Height} PixelFormat={PixelFormat} Channel={Channel} RawBytes={RawBytes} Czi={Czi} Tiff={Tiff}",
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
                Width = frame?.Width ?? 0,
                Height = frame?.Height ?? 0,
                PixelFormat = frame?.PixelFormat.ToString(),
                Channel = capturedChannel,
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
        CancellationToken token)
    {
        try
        {
            using var call = streaming.MonitorExperiment(
                new ExperimentStreamingServiceMonitorExperimentRequest
                {
                    ExperimentId = experimentId,
                    ChannelIndex = _options.ChannelIndex,
                    EnableRawData = false,
                },
                headers: metadata,
                cancellationToken: token);

            _decoder.Reset();
            ready.TrySetResult();
            await foreach (var response in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                reportChannel(response.FrameData?.FramePosition?.C);
                var frame = _decoder.Add(response.FrameData, enableRawData: false, out _);
                if (frame is not null)
                    return frame;
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

    /// <summary>
    /// Blocks until the live stream delivers a frame newer than the one present at call time.
    /// Fails if live is down or the stream dies while waiting (avoids returning a stale frame).
    /// </summary>
    private async Task<CameraFrame> WaitForFreshFrameAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset threshold;
        lock (_gate)
        {
            if (!_connected)
                throw new InvalidOperationException("ZEN camera is not connected.");
            if (!_live)
                throw new InvalidOperationException("ZEN live stream is not running. Start live, then capture.");
            threshold = _lastFrame?.CapturedAt ?? DateTimeOffset.MinValue;
        }

        const int timeoutMs = 15_000;
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (!_live)
                {
                    throw new InvalidOperationException(
                        "ZEN live stream ended before a fresh frame arrived. Capture aborted to avoid stale data.");
                }

                if (_lastFrame is not null && _lastFrame.CapturedAt > threshold)
                    return _lastFrame;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Timed out waiting for a fresh ZEN frame after the stage move. Capture aborted to avoid stale data.");
    }

    private async Task<bool> TryStartAcquisitionAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        bool live,
        CancellationToken cancellationToken)
    {
        try
        {
            await StartAcquisitionOnceAsync(experiment, metadata, experimentId, live, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RpcException ex) when (IsAlreadyActive(ex))
        {
            SetStatus("ZEN: experiment already active — stopping and retrying...");
            await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);
            await StartAcquisitionOnceAsync(experiment, metadata, experimentId, live, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RpcException ex) when (IsControllingDenied(ex))
        {
            _logger.LogWarning(ex, "ZEN controlling call denied");
            SetStatus(
                "ZEN API control is locked. Enable Unsupervised API Mode (ZEN: Tools → Options → ZEN API), " +
                "or start Live in ZEN — this app will display that stream.");
            return false;
        }
    }

    private static Task StartAcquisitionOnceAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        bool live,
        CancellationToken cancellationToken)
    {
        if (live)
        {
            // Omit track index so ZEN uses the activated camera track.
            // Forcing 0 selects an empty track when the Axiocam is not track 0, and live then has no pixels.
            return experiment.StartLiveAsync(
                new ExperimentServiceStartLiveRequest { ExperimentId = experimentId },
                headers: metadata,
                cancellationToken: cancellationToken).ResponseAsync;
        }

        return experiment.StartContinuousAsync(
            new ExperimentServiceStartContinuousRequest { ExperimentId = experimentId },
            headers: metadata,
            cancellationToken: cancellationToken).ResponseAsync;
    }

    private async Task<bool> WatchForFramesAsync(
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        bool controlling,
        CancellationToken liveToken,
        CancellationToken cancellationToken)
    {
        // Official zenapi_streaming.py keeps one stream open and reads complete frames
        // (enable_raw_data false, shaped by FrameSize). Partial/raw mode is only a fallback.
        // API-started acquisition is MonitorExperiment; Live started in the ZEN window is
        // MonitorAllExperiments. A full Axiocam frame is one gRPC message — do not cancel
        // that call on a short timer while bytes are still arriving.
        var attempts = controlling
            ? new (bool AllExperiments, bool EnableRawData, string Label, TimeSpan Timeout)[]
            {
                (false, false, "MonitorExperiment", TimeSpan.FromSeconds(12)),
                (false, true, "MonitorExperiment partial frames", TimeSpan.FromSeconds(10)),
                (true, false, "MonitorAllExperiments", TimeSpan.FromSeconds(8)),
            }
            : new (bool AllExperiments, bool EnableRawData, string Label, TimeSpan Timeout)[]
            {
                (true, false, "MonitorAllExperiments", TimeSpan.FromSeconds(12)),
                (true, true, "MonitorAllExperiments partial frames", TimeSpan.FromSeconds(8)),
                (false, false, "MonitorExperiment", TimeSpan.FromSeconds(8)),
            };

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveToken.ThrowIfCancellationRequested();
            if (_frameTooLarge)
                return false;

            SetStatus($"ZEN waiting for pixels ({attempt.Label})...");
            var hit = await MonitorOnceAsync(
                streaming,
                metadata,
                experimentId,
                attempt.AllExperiments,
                attempt.EnableRawData,
                attempt.Timeout,
                liveToken,
                cancellationToken).ConfigureAwait(false);

            if (hit == MonitorHit.Frame)
                return true;
        }

        return false;
    }

    private async Task<MonitorHit> MonitorOnceAsync(
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        bool allExperiments,
        bool enableRawData,
        TimeSpan timeout,
        CancellationToken liveToken,
        CancellationToken cancellationToken)
    {
        var attemptId = Interlocked.Increment(ref _attemptId);
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(liveToken);
        var first = new TaskCompletionSource<MonitorHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decoder.Reset();
        var loop = Task.Run(
            () => ConsumeAsync(streaming, metadata, experimentId, allExperiments, enableRawData, attemptId, first, attempt.Token),
            CancellationToken.None);

        lock (_gate)
        {
            _streamCts = attempt;
            _liveLoop = loop;
        }

        try
        {
            var hit = await first.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            liveToken.ThrowIfCancellationRequested();
            if (hit == MonitorHit.Frame && attemptId == Volatile.Read(ref _attemptId))
                return MonitorHit.Frame;

            await CancelAttemptAsync(attempt, loop).ConfigureAwait(false);
            return hit == MonitorHit.Frame ? MonitorHit.None : hit;
        }
        catch (TimeoutException)
        {
            await CancelAttemptAsync(attempt, loop).ConfigureAwait(false);
            return MonitorHit.None;
        }
        catch (OperationCanceledException)
        {
            await CancelAttemptAsync(attempt, loop).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ZEN monitor failed");
            await CancelAttemptAsync(attempt, loop).ConfigureAwait(false);
            return MonitorHit.Failed;
        }
    }

    private async Task CancelAttemptAsync(CancellationTokenSource attempt, Task loop)
    {
        CancelQuiet(attempt);
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }

        lock (_gate)
        {
            if (ReferenceEquals(_streamCts, attempt))
                _streamCts = null;
            if (_liveLoop == loop)
                _liveLoop = null;
        }

        DisposeQuiet(attempt);
    }

    private async Task ConsumeAsync(
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        bool allExperiments,
        bool enableRawData,
        int attemptId,
        TaskCompletionSource<MonitorHit> first,
        CancellationToken token)
    {
        var sourceName = allExperiments ? "MonitorAllExperiments" : "MonitorExperiment";
        try
        {
            var channelIndex = _options.ChannelIndex;
            using var call = allExperiments
                ? streaming.MonitorAllExperiments(
                    new ExperimentStreamingServiceMonitorAllExperimentsRequest
                    {
                        ChannelIndex = channelIndex,
                        EnableRawData = enableRawData,
                    },
                    headers: metadata,
                    cancellationToken: token)
                : null;

            using var experimentCall = allExperiments
                ? null
                : streaming.MonitorExperiment(
                    new ExperimentStreamingServiceMonitorExperimentRequest
                    {
                        ExperimentId = experimentId,
                        ChannelIndex = channelIndex,
                        EnableRawData = enableRawData,
                    },
                    headers: metadata,
                    cancellationToken: token);

            SetStatus($"ZEN {sourceName} stream open (complete frames {!enableRawData}, channel {channelIndex})");

            if (allExperiments)
            {
                await foreach (var response in call!.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
                    Publish(response.FrameData, sourceName, enableRawData, attemptId, first);
            }
            else
            {
                await foreach (var response in experimentCall!.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
                    Publish(response.FrameData, sourceName, enableRawData, attemptId, first);
            }

            NoteKeptStreamEnded(first);
        }
        catch (OperationCanceledException)
        {
            first.TrySetCanceled(token);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            first.TrySetCanceled();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            _frameTooLarge = true;
            var message = "ZEN frame is larger than the gateway/client receive limit. Full Axiocam frames need a raised gRPC size (this app allows 256 MB).";
            _logger.LogError(ex, message);
            SetStatus(message);
            first.TrySetException(ex);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            _logger.LogWarning(ex, "ZEN {Source} is not implemented on this Gateway", sourceName);
            SetStatus($"ZEN {sourceName} is not implemented on this ZEN build");
            first.TrySetResult(MonitorHit.Failed);
        }
        catch (Exception ex)
        {
            if (KeptStreamEnded(first))
            {
                _logger.LogWarning(ex, "ZEN {Source} ended", sourceName);
                SetStatus($"ZEN pixel stream ended: {ex.Message}");
                return;
            }

            _logger.LogWarning(ex, "ZEN {Source} ended with error", sourceName);
            SetStatus($"ZEN {sourceName} error: {ex.Message}");
            first.TrySetException(ex);
        }
    }

    private void NoteKeptStreamEnded(TaskCompletionSource<MonitorHit> first)
    {
        if (KeptStreamEnded(first))
            SetStatus("ZEN pixel stream ended");
        else
            first.TrySetResult(MonitorHit.None);
    }

    private bool KeptStreamEnded(TaskCompletionSource<MonitorHit> first)
    {
        if (!first.Task.IsCompletedSuccessfully || first.Task.Result != MonitorHit.Frame)
            return false;

        lock (_gate) _live = false;
        return true;
    }

    private void Publish(
        FrameData? data,
        string sourceName,
        bool enableRawData,
        int attemptId,
        TaskCompletionSource<MonitorHit> first)
    {
        if (attemptId != Volatile.Read(ref _attemptId))
            return;

        var count = Interlocked.Increment(ref _streamMessages);
        var frame = _decoder.Add(data, enableRawData, out var skipReason);
        if (attemptId != Volatile.Read(ref _attemptId))
            return;

        if (frame is null)
        {
            if (!string.IsNullOrEmpty(skipReason))
                _lastSkip = skipReason;

            // One empty prelude is normal. Leave the stream open so the following full frame can arrive.
            var giveUp = skipReason.Contains("RawData is empty", StringComparison.Ordinal) && count >= (enableRawData ? 40 : 8)
                || skipReason.Contains("expected", StringComparison.Ordinal) && count >= (enableRawData ? 20 : 5);
            if (giveUp)
                first.TrySetResult(MonitorHit.NeedRawData);
            else if (!string.IsNullOrEmpty(skipReason)
                     && !skipReason.StartsWith("buffering", StringComparison.Ordinal)
                     && count is 1 or 10 or 50)
                SetStatus($"ZEN {sourceName} message {count}: {skipReason}");
            return;
        }

        lock (_gate) _lastFrame = frame;
        if (first.TrySetResult(MonitorHit.Frame))
            SetStatus($"ZEN live frame from {sourceName} ({frame.Width}x{frame.Height} {frame.PixelFormat})");

        try
        {
            FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live frame handler failed");
        }
    }

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

    private static bool IsCancelled(CancellationTokenSource cts)
    {
        try { return cts.IsCancellationRequested; }
        catch (ObjectDisposedException) { return true; }
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

    private enum MonitorHit
    {
        None = 0,
        Frame = 1,
        NeedRawData = 2,
        Failed = 3,
    }
}
