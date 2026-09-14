using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Infrastructure.Cameras;

/// <summary>
/// Offline stand-in that synthesizes a gray ramp frame for live preview / capture.
/// </summary>
public sealed class PlaceholderCameraService : ICameraService
{
    private readonly object _gate = new();
    private bool _connected;
    private bool _live;
    private CancellationTokenSource? _liveCts;
    private Task? _liveLoop;
    private CameraFrame? _lastFrame;
    private int _frameCounter;

    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (!_connected) return "Simulator camera - disconnected";
                if (_live) return "Simulator camera - live";
                return "Simulator camera - connected";
            }
        }
    }

    public bool IsConnected { get { lock (_gate) return _connected; } }
    public bool IsLive { get { lock (_gate) return _live; } }
    public bool IsPreviewAvailable => true;

    public event EventHandler<CameraFrameEventArgs>? FrameReceived;

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _connected = true;
        return Task.FromResult(true);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopLiveAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) _connected = false;
    }

    public Task StartLiveAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_connected) throw new InvalidOperationException("Connect the camera first.");
            if (_live) return Task.CompletedTask;
            _live = true;
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;
            _liveLoop = Task.Run(() => LiveLoopAsync(token), token);
        }
        return Task.CompletedTask;
    }

    public async Task StopLiveAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _liveCts;
            loop = _liveLoop;
            _liveCts = null;
            _liveLoop = null;
            _live = false;
        }

        if (cts is null) return;
        cts.Cancel();
        try
        {
            if (loop is not null) await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); }
    }

    public Task<byte[]> CaptureStillAsync(CancellationToken cancellationToken = default)
    {
        var frame = GetOrCreateFrame();
        return Task.FromResult(MonoFrameEncoder.ToPng(frame));
    }

    public Task<byte[]> GetPreviewFrameAsync(CancellationToken cancellationToken = default)
    {
        var frame = GetOrCreateFrame();
        return Task.FromResult(MonoFrameEncoder.ToPng(frame));
    }

    private async Task LiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frame = CreateFrame();
            lock (_gate) _lastFrame = frame;
            FrameReceived?.Invoke(this, new CameraFrameEventArgs(frame));
            try { await Task.Delay(100, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private CameraFrame GetOrCreateFrame()
    {
        lock (_gate)
        {
            return _lastFrame ?? CreateFrame();
        }
    }

    private CameraFrame CreateFrame()
    {
        const int w = 640;
        const int h = 480;
        var pixels = new byte[w * h];
        var phase = Interlocked.Increment(ref _frameCounter);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                pixels[y * w + x] = (byte)((x + y + phase * 3) & 0xFF);
            }
        }

        return new CameraFrame
        {
            RawPixels = pixels,
            Width = w,
            Height = h,
            PixelFormat = CameraPixelFormat.Gray8,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }
}