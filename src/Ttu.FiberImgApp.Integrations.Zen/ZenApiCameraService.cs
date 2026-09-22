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
/// Live pixels are possible: ZEN acquires the camera and ExperimentStreamingService
/// pushes FrameData. This app does not talk to the Axiocam over USB.
/// Stills are encoded from the latest streamed frame (PNG).
/// </summary>
public sealed class ZenApiCameraService : ICameraService, IZenClient, IAsyncDisposable
{
    private readonly ZenOptions _options;
    private readonly ILogger<ZenApiCameraService> _logger;
    private readonly IActivityLog _activityLog;
    private readonly object _gate = new();
    private readonly LiveFrameAssembler _assembler = new();

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
            if (_live) return;
            experimentId = _experimentId;
            experiment = _experiment;
            streaming = _streaming;
            metadata = _metadata;
            _frameTooLarge = false;
            _streamMessages = 0;
            _lastSkip = string.Empty;
        }

        await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _liveCts = cts;
            _live = true;
            _assembler.Reset();
        }

        // The pixel stream must stay tied to _liveCts. Disposing a linked token when StartLiveAsync
        // returns would cancel the stream as soon as the first frame arrived.
        try
        {
            var controlling = await TryStartAcquisitionAsync(experiment, metadata, experimentId, live: true, cancellationToken)
                .ConfigureAwait(false);

            if (await WatchForFramesAsync(streaming, metadata, experimentId, cts.Token, cancellationToken).ConfigureAwait(false))
            {
                SetStatus(controlling ? "ZEN live running" : "ZEN live running (stream from ZEN UI)");
                return;
            }

            if (_frameTooLarge)
                throw new InvalidOperationException(Status);

            if (controlling)
            {
                SetStatus("ZEN StartLive produced no frames — stopping, then StartContinuous...");
                await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);
                _assembler.Reset();
                await TryStartAcquisitionAsync(experiment, metadata, experimentId, live: false, cancellationToken)
                    .ConfigureAwait(false);

                if (await WatchForFramesAsync(streaming, metadata, experimentId, cts.Token, cancellationToken).ConfigureAwait(false))
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

        streamCts?.Cancel();
        cts?.Cancel();
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            streamCts?.Dispose();
            cts?.Dispose();
        }

        _assembler.Reset();

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
        var frame = await WaitForFreshFrameAsync(cancellationToken).ConfigureAwait(false);
        return MonoFrameEncoder.ToPng(frame);
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
        CancellationToken liveToken,
        CancellationToken cancellationToken)
    {
        // LM cameras (Axiocam) emit a full frame per message. Cancelling the stream every few
        // seconds aborts that transfer before gRPC can deliver it, which looks like "no pixels".
        var attempts = new (bool AllExperiments, bool EnableRawData, string Label, TimeSpan Timeout)[]
        {
            (false, true, "MonitorExperiment", TimeSpan.FromSeconds(20)),
            (true, true, "MonitorAllExperiments", TimeSpan.FromSeconds(15)),
            (false, false, "MonitorExperiment assembled frames", TimeSpan.FromSeconds(12)),
        };

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (hit == MonitorHit.NeedRawData)
                continue;
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
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(liveToken);
        var first = new TaskCompletionSource<MonitorHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        _assembler.Reset();
        var loop = Task.Run(
            () => ConsumeAsync(streaming, metadata, experimentId, allExperiments, enableRawData, first, attempt.Token));

        try
        {
            var hit = await first.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (hit == MonitorHit.Frame)
            {
                lock (_gate)
                {
                    _streamCts = attempt;
                    _liveLoop = loop;
                }

                return MonitorHit.Frame;
            }

            await CancelAttemptAsync(attempt, loop).ConfigureAwait(false);
            return hit;
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

    private static async Task CancelAttemptAsync(CancellationTokenSource attempt, Task loop)
    {
        attempt.Cancel();
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally { attempt.Dispose(); }
    }

    private async Task ConsumeAsync(
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        bool allExperiments,
        bool enableRawData,
        TaskCompletionSource<MonitorHit> first,
        CancellationToken token)
    {
        var sourceName = allExperiments ? "MonitorAllExperiments" : "MonitorExperiment";
        try
        {
            using var call = allExperiments
                ? streaming.MonitorAllExperiments(
                    new ExperimentStreamingServiceMonitorAllExperimentsRequest { EnableRawData = enableRawData },
                    headers: metadata,
                    cancellationToken: token)
                : null;

            using var experimentCall = allExperiments
                ? null
                : streaming.MonitorExperiment(
                    new ExperimentStreamingServiceMonitorExperimentRequest
                    {
                        ExperimentId = experimentId,
                        EnableRawData = enableRawData,
                    },
                    headers: metadata,
                    cancellationToken: token);

            SetStatus($"ZEN {sourceName} stream open (raw data {enableRawData})");

            if (allExperiments)
            {
                await foreach (var response in call!.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
                    Publish(response.FrameData, sourceName, enableRawData, first);
            }
            else
            {
                await foreach (var response in experimentCall!.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
                    Publish(response.FrameData, sourceName, enableRawData, first);
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

    private void Publish(FrameData? data, string sourceName, bool enableRawData, TaskCompletionSource<MonitorHit> first)
    {
        var count = Interlocked.Increment(ref _streamMessages);
        var frame = _assembler.Add(data, out var skipReason);
        if (frame is null)
        {
            if (!string.IsNullOrEmpty(skipReason))
                _lastSkip = skipReason;

            // One empty prelude is normal. Leave the stream open so the following full frame can arrive.
            if (skipReason.Contains("RawData is empty", StringComparison.Ordinal) && count >= (enableRawData ? 40 : 8))
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
        FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
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
        if (channel is not null)
            await channel.ShutdownAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Stitches Gateway tiles / scan lines into one image. A full-frame packet is returned as-is.
    /// </summary>
    private sealed class LiveFrameAssembler
    {
        private byte[] _pixels = [];
        private int _width;
        private int _height;
        private CameraPixelFormat _format;
        private int _bpp;
        private long _lastEmitTick;

        public void Reset()
        {
            _pixels = [];
            _width = 0;
            _height = 0;
            _bpp = 0;
            _lastEmitTick = 0;
        }

        public CameraFrame? Add(FrameData? data, out string skipReason)
        {
            skipReason = string.Empty;
            if (data?.PixelData is null)
            {
                skipReason = "PixelData is null";
                return null;
            }

            var raw = data.PixelData.RawData.ToByteArray();
            if (raw.Length == 0)
            {
                skipReason = "RawData is empty";
                return null;
            }

            var format = data.PixelData.PixelType switch
            {
                PixelType.Gray8 => CameraPixelFormat.Gray8,
                PixelType.Gray16 => CameraPixelFormat.Gray16,
                PixelType.Bgr24 => CameraPixelFormat.Bgr24,
                PixelType.Bgr48 => CameraPixelFormat.Bgr48,
                _ => InferFormat(raw.Length, data),
            };
            var bpp = BytesPerPixel(format);
            if (bpp <= 0)
            {
                skipReason = $"Unsupported pixel type {data.PixelData.PixelType}";
                return null;
            }

            var tileW = data.PixelData.Size?.Width ?? 0;
            var tileH = data.PixelData.Size?.Height ?? 0;
            var fullW = data.FrameSize?.Width ?? 0;
            var fullH = data.FrameSize?.Height ?? 0;
            if (tileW <= 0) tileW = fullW;
            if (tileH <= 0) tileH = fullH;
            if (fullW <= 0) fullW = tileW;
            if (fullH <= 0) fullH = tileH;
            if (tileW <= 0 || tileH <= 0 || fullW <= 0 || fullH <= 0)
            {
                skipReason = "Width/Height missing on frame";
                return null;
            }

            var expected = checked(tileW * tileH * bpp);
            if (raw.Length < expected)
            {
                skipReason = $"Got {raw.Length} bytes, expected >= {expected} for {tileW}x{tileH} {format}";
                return null;
            }

            var startX = data.PixelData.StartPosition?.X ?? 0;
            var startY = data.PixelData.StartPosition?.Y ?? 0;
            if (data.PixelData.StartPosition is null && (tileW < fullW || tileH < fullH))
            {
                startX = data.FramePosition?.X ?? 0;
                startY = data.FramePosition?.Y ?? 0;
            }

            if (startX == 0 && startY == 0 && tileW == fullW && tileH == fullH)
            {
                Reset();
                return new CameraFrame
                {
                    RawPixels = raw,
                    Width = fullW,
                    Height = fullH,
                    PixelFormat = format,
                    CapturedAt = DateTimeOffset.UtcNow,
                };
            }

            EnsureCanvas(fullW, fullH, format, bpp);
            Blit(raw, startX, startY, tileW, tileH);

            var now = Environment.TickCount64;
            var completed = startY + tileH >= _height && startX + tileW >= _width;
            if (!completed && now - _lastEmitTick < 120)
            {
                skipReason = "buffering scan lines";
                return null;
            }

            _lastEmitTick = now;
            return new CameraFrame
            {
                RawPixels = (byte[])_pixels.Clone(),
                Width = _width,
                Height = _height,
                PixelFormat = _format,
                CapturedAt = DateTimeOffset.UtcNow,
            };
        }

        private void EnsureCanvas(int width, int height, CameraPixelFormat format, int bpp)
        {
            if (_width == width && _height == height && _format == format && _pixels.Length == width * height * bpp)
                return;

            _width = width;
            _height = height;
            _format = format;
            _bpp = bpp;
            _pixels = new byte[checked(width * height * bpp)];
            _lastEmitTick = 0;
        }

        private void Blit(byte[] src, int startX, int startY, int tileW, int tileH)
        {
            var srcStride = tileW * _bpp;
            var dstStride = _width * _bpp;
            for (var row = 0; row < tileH; row++)
            {
                var dy = startY + row;
                if (dy < 0 || dy >= _height) continue;
                var dx = Math.Max(0, startX);
                var srcOffset = (row * srcStride) + ((dx - startX) * _bpp);
                var copy = Math.Min(srcStride - ((dx - startX) * _bpp), (_width - dx) * _bpp);
                if (copy <= 0 || srcOffset < 0 || srcOffset + copy > src.Length) continue;
                Buffer.BlockCopy(src, srcOffset, _pixels, (dy * dstStride) + (dx * _bpp), copy);
            }
        }

        private static CameraPixelFormat InferFormat(int byteLength, FrameData data)
        {
            var width = data.PixelData?.Size?.Width > 0 ? data.PixelData.Size.Width : data.FrameSize?.Width ?? 0;
            var height = data.PixelData?.Size?.Height > 0 ? data.PixelData.Size.Height : data.FrameSize?.Height ?? 0;
            if (width <= 0 || height <= 0) return CameraPixelFormat.Gray8;

            var pixels = width * height;
            if (byteLength >= pixels * 6) return CameraPixelFormat.Bgr48;
            if (byteLength >= pixels * 3) return CameraPixelFormat.Bgr24;
            if (byteLength >= pixels * 2) return CameraPixelFormat.Gray16;
            return CameraPixelFormat.Gray8;
        }

        private static int BytesPerPixel(CameraPixelFormat format) => format switch
        {
            CameraPixelFormat.Gray8 => 1,
            CameraPixelFormat.Gray16 => 2,
            CameraPixelFormat.Bgr24 => 3,
            CameraPixelFormat.Bgr48 => 6,
            _ => 0,
        };
    }
}
