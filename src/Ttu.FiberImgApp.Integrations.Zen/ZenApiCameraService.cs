using Google.Protobuf.WellKnownTypes;
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
/// Axiocam / ZEN live + still capture via ZEN API Gateway (gRPC).
/// Stills are encoded from the latest streamed frame (PNG).
/// </summary>
public sealed class ZenApiCameraService : ICameraService, IZenClient, IAsyncDisposable
{
    private readonly ZenOptions _options;
    private readonly ILogger<ZenApiCameraService> _logger;
    private readonly IActivityLog _activityLog;
    private readonly object _gate = new();

    private GrpcChannel? _channel;
    private ExperimentService.ExperimentServiceClient? _experiment;
    private ExperimentStreamingService.ExperimentStreamingServiceClient? _streaming;
    private Metadata? _metadata;
    private string? _experimentId;
    private CancellationTokenSource? _liveCts;
    private Task? _liveLoop;
    private CameraFrame? _lastFrame;
    private string _status = "ZEN camera - disconnected";
    private bool _connected;
    private bool _live;

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
                SetStatus("ZEN: missing ApiToken / ApiTokenFilePath");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_options.ExperimentName))
            {
                SetStatus("ZEN: set ExperimentName (Axiocam .czexp without extension)");
                return false;
            }

            await DisposeChannelAsync().ConfigureAwait(false);

            _channel = ZenChannelFactory.Create(_options);
            _metadata = new Metadata { { "control-token", token } };
            _experiment = new ExperimentService.ExperimentServiceClient(_channel);
            _streaming = new ExperimentStreamingService.ExperimentStreamingServiceClient(_channel);

            var load = await _experiment.LoadAsync(
                new ExperimentServiceLoadRequest { ExperimentName = _options.ExperimentName },
                headers: _metadata,
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

            lock (_gate)
            {
                _experimentId = load.ExperimentId;
                _connected = true;
            }

            // Ensure we don't inherit a Live session left running from a previous attempt.
            await TryStopExperimentAsync(_experiment, _metadata, load.ExperimentId, cancellationToken)
                .ConfigureAwait(false);

            SetStatus($"ZEN connected - experiment '{_options.ExperimentName}' (gateway {_options.Host}:{_options.Port})");
            return true;
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
        }

        // Clear a leftover Live/Continuous from a prior Start live (causes "same ID is already active").
        await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        var streamReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _liveCts = cts;
            _live = true;
            _liveLoop = Task.Run(
                () => StreamLoopAsync(streaming, metadata, experimentId, streamReady, firstFrame, cts.Token),
                cts.Token);
        }

        try
        {
            try
            {
                await streamReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                SetStatus("ZEN: timed out opening pixel monitor stream");
            }

            await StartAcquisitionAsync(experiment, metadata, experimentId, live: true, cancellationToken)
                .ConfigureAwait(false);
            SetStatus("ZEN live started — waiting for frames...");

            if (await WaitForFirstFrameAsync(firstFrame, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false))
            {
                SetStatus("ZEN live running");
                return;
            }

            // StartLive left the experiment active — must Stop before Continuous.
            SetStatus("ZEN StartLive: no frames — stopping, then StartContinuous...");
            await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);

            await StartAcquisitionAsync(experiment, metadata, experimentId, live: false, cancellationToken)
                .ConfigureAwait(false);
            SetStatus("ZEN continuous started — waiting for frames...");

            if (await WaitForFirstFrameAsync(firstFrame, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false))
            {
                SetStatus("ZEN live running (continuous)");
                return;
            }

            SetStatus(
                "ZEN acquisition started but no pixel frames arrived. " +
                "Confirm Fiber Img App Gateway port matches the Gateway UI (often 50051 or 5002), " +
                "stop Live in ZEN, enable API mode if available, then retry.");
        }
        catch
        {
            await StopLiveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> WaitForFirstFrameAsync(
        TaskCompletionSource firstFrame,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await firstFrame.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task StartAcquisitionAsync(
        ExperimentService.ExperimentServiceClient experiment,
        Metadata metadata,
        string experimentId,
        bool live,
        CancellationToken cancellationToken)
    {
        try
        {
            if (live)
            {
                await experiment.StartLiveAsync(
                    new ExperimentServiceStartLiveRequest
                    {
                        ExperimentId = experimentId,
                        TrackIndex = 0,
                    },
                    headers: metadata,
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
            else
            {
                await experiment.StartContinuousAsync(
                    new ExperimentServiceStartContinuousRequest { ExperimentId = experimentId },
                    headers: metadata,
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
        }
        catch (RpcException ex) when (IsAlreadyActive(ex))
        {
            SetStatus("ZEN: experiment already active — stopping and retrying...");
            await TryStopExperimentAsync(experiment, metadata, experimentId, cancellationToken).ConfigureAwait(false);
            if (live)
            {
                await experiment.StartLiveAsync(
                    new ExperimentServiceStartLiveRequest
                    {
                        ExperimentId = experimentId,
                        TrackIndex = 0,
                    },
                    headers: metadata,
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
            else
            {
                await experiment.StartContinuousAsync(
                    new ExperimentServiceStartContinuousRequest { ExperimentId = experimentId },
                    headers: metadata,
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
        }
    }

    private static bool IsAlreadyActive(RpcException ex) =>
        ex.Status.Detail.Contains("already active", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already active", StringComparison.OrdinalIgnoreCase);

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

    public async Task StopLiveAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        string? experimentId;
        ExperimentService.ExperimentServiceClient? experiment;
        Metadata? metadata;

        lock (_gate)
        {
            cts = _liveCts;
            loop = _liveLoop;
            experimentId = _experimentId;
            experiment = _experiment;
            metadata = _metadata;
            _liveCts = null;
            _liveLoop = null;
            _live = false;
        }

        if (cts is not null)
        {
            cts.Cancel();
            try { if (loop is not null) await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { cts.Dispose(); }
        }

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
        // Wait for a frame acquired after this call so sampling does not save a pre-move exposure.
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

    private async Task StreamLoopAsync(
        ExperimentStreamingService.ExperimentStreamingServiceClient streaming,
        Metadata metadata,
        string experimentId,
        TaskCompletionSource streamReady,
        TaskCompletionSource firstFrame,
        CancellationToken token)
    {
        try
        {
            // Run both monitors: some ZEN builds only emit Live pixels on MonitorAllExperiments.
            var experimentMonitor = ConsumeStreamAsync(
                () =>
                {
                    var request = new ExperimentStreamingServiceMonitorExperimentRequest
                    {
                        ExperimentId = experimentId,
                        EnableRawData = true,
                    };
                    // Omit ChannelIndex so ZEN returns all channels (Zeiss default).
                    return streaming.MonitorExperiment(request, headers: metadata, cancellationToken: token);
                },
                "MonitorExperiment",
                streamReady,
                firstFrame,
                token);

            var allMonitor = ConsumeStreamAsync(
                () =>
                {
                    var request = new ExperimentStreamingServiceMonitorAllExperimentsRequest
                    {
                        EnableRawData = true,
                    };
                    return streaming.MonitorAllExperiments(request, headers: metadata, cancellationToken: token);
                },
                "MonitorAllExperiments",
                streamReady,
                firstFrame,
                token);

            await Task.WhenAll(experimentMonitor, allMonitor).ConfigureAwait(false);

            if (!firstFrame.Task.IsCompleted)
            {
                SetStatus("ZEN stream ended without any usable pixel frames");
                firstFrame.TrySetException(new InvalidOperationException("ZEN stream ended without frames."));
            }
        }
        catch (OperationCanceledException)
        {
            streamReady.TrySetCanceled(token);
            firstFrame.TrySetCanceled(token);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            streamReady.TrySetCanceled();
            firstFrame.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZEN stream failed");
            SetStatus($"ZEN stream error: {ex.Message}");
            streamReady.TrySetException(ex);
            firstFrame.TrySetException(ex);
            lock (_gate) _live = false;
        }
    }

    private async Task ConsumeStreamAsync<TResponse>(
        Func<AsyncServerStreamingCall<TResponse>> startCall,
        string sourceName,
        TaskCompletionSource streamReady,
        TaskCompletionSource firstFrame,
        CancellationToken token)
        where TResponse : class
    {
        var skippedEmpty = 0;
        try
        {
            using var call = startCall();
            streamReady.TrySetResult();
            SetStatus($"ZEN {sourceName} stream open");

            await foreach (var response in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                var frameData = ExtractFrameData(response);
                var frame = ToCameraFrame(frameData, out var skipReason);
                if (frame is null)
                {
                    skippedEmpty++;
                    if (skippedEmpty is 1 or 10 or 50)
                    {
                        _logger.LogWarning(
                            "ZEN {Source} skipped frame ({Count}): {Reason}",
                            sourceName,
                            skippedEmpty,
                            skipReason);
                        SetStatus($"ZEN {sourceName}: skipping frames ({skipReason})");
                    }

                    continue;
                }

                lock (_gate) _lastFrame = frame;
                if (firstFrame.TrySetResult())
                    SetStatus($"ZEN live frame from {sourceName} ({frame.Width}x{frame.Height} {frame.PixelFormat})");
                FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            _logger.LogWarning(ex, "ZEN {Source} unimplemented on this Gateway/ZEN build", sourceName);
            SetStatus($"ZEN {sourceName} unimplemented on this ZEN build");
            streamReady.TrySetResult(); // allow StartLive to proceed using the other monitor
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ZEN {Source} ended with error", sourceName);
            SetStatus($"ZEN {sourceName} error: {ex.Message}");
            streamReady.TrySetResult();
        }
    }

    private static FrameData? ExtractFrameData<TResponse>(TResponse response)
    {
        return response switch
        {
            ExperimentStreamingServiceMonitorExperimentResponse exp => exp.FrameData,
            ExperimentStreamingServiceMonitorAllExperimentsResponse all => all.FrameData,
            _ => null,
        };
    }

    private static CameraFrame? ToCameraFrame(FrameData? data, out string skipReason)
    {
        skipReason = string.Empty;
        if (data is null)
        {
            skipReason = "FrameData is null";
            return null;
        }

        if (data.PixelData is null)
        {
            skipReason = "PixelData is null";
            return null;
        }

        var pixels = data.PixelData.RawData.ToByteArray();
        if (pixels.Length == 0)
        {
            skipReason = "RawData is empty (try EnableRawData / check experiment is acquiring)";
            return null;
        }

        var format = data.PixelData.PixelType switch
        {
            PixelType.Gray8 => CameraPixelFormat.Gray8,
            PixelType.Gray16 => CameraPixelFormat.Gray16,
            PixelType.Bgr24 => CameraPixelFormat.Bgr24,
            PixelType.Bgr48 => CameraPixelFormat.Bgr48,
            PixelType.Unspecified => InferFormatFromByteLength(pixels.Length, data),
            _ => InferFormatFromByteLength(pixels.Length, data),
        };

        var width = data.PixelData.Size?.Width > 0
            ? data.PixelData.Size.Width
            : data.FrameSize?.Width ?? 0;
        var height = data.PixelData.Size?.Height > 0
            ? data.PixelData.Size.Height
            : data.FrameSize?.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            skipReason = "Width/Height missing on frame";
            return null;
        }

        var expected = ExpectedByteLength(format, width, height);
        if (expected > 0 && pixels.Length < expected)
        {
            skipReason = $"Got {pixels.Length} bytes, expected >= {expected} for {width}x{height} {format}";
            return null;
        }

        return new CameraFrame
        {
            RawPixels = pixels,
            Width = width,
            Height = height,
            PixelFormat = format,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private static CameraPixelFormat InferFormatFromByteLength(int byteLength, FrameData data)
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

    private static int ExpectedByteLength(CameraPixelFormat format, int width, int height)
    {
        var pixels = checked(width * height);
        return format switch
        {
            CameraPixelFormat.Gray8 => pixels,
            CameraPixelFormat.Gray16 => pixels * 2,
            CameraPixelFormat.Bgr24 => pixels * 3,
            CameraPixelFormat.Bgr48 => pixels * 6,
            _ => 0,
        };
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
}