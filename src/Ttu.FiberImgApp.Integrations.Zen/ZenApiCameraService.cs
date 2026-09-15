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

            SetStatus($"ZEN connected - experiment '{_options.ExperimentName}'");
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

        await experiment.StartLiveAsync(
            new ExperimentServiceStartLiveRequest { ExperimentId = experimentId },
            headers: metadata,
            cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _liveCts = cts;
            _live = true;
            _liveLoop = Task.Run(() => StreamLoopAsync(streaming, metadata, experimentId, cts.Token), cts.Token);
        }

        SetStatus("ZEN live running");
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
        CancellationToken token)
    {
        try
        {
            var request = new ExperimentStreamingServiceMonitorExperimentRequest
            {
                ExperimentId = experimentId,
                EnableRawData = false,
                ChannelIndex = _options.ChannelIndex,
            };

            using var call = streaming.MonitorExperiment(request, headers: metadata, cancellationToken: token);
            await foreach (var response in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                var frame = ToCameraFrame(response.FrameData);
                if (frame is null) continue;

                lock (_gate) _lastFrame = frame;
                FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZEN stream failed");
            SetStatus($"ZEN stream error: {ex.Message}");
            lock (_gate) _live = false;
        }
    }

    private static CameraFrame? ToCameraFrame(FrameData? data)
    {
        if (data?.PixelData is null || data.FrameSize is null) return null;
        var pixels = data.PixelData.RawData.ToByteArray();
        if (pixels.Length == 0) return null;

        var format = data.PixelData.PixelType switch
        {
            PixelType.Gray8 => CameraPixelFormat.Gray8,
            PixelType.Gray16 => CameraPixelFormat.Gray16,
            PixelType.Bgr24 => CameraPixelFormat.Bgr24,
            PixelType.Bgr48 => CameraPixelFormat.Bgr48,
            _ => CameraPixelFormat.Gray16,
        };

        var width = data.PixelData.Size?.Width > 0 ? data.PixelData.Size.Width : data.FrameSize.Width;
        var height = data.PixelData.Size?.Height > 0 ? data.PixelData.Size.Height : data.FrameSize.Height;
        if (width <= 0 || height <= 0) return null;

        return new CameraFrame
        {
            RawPixels = pixels,
            Width = width,
            Height = height,
            PixelFormat = format,
            CapturedAt = DateTimeOffset.UtcNow,
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