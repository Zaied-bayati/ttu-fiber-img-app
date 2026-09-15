using Microsoft.Extensions.Logging.Abstractions;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Core.Sampling;
using Ttu.FiberImgApp.Infrastructure.Sampling;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public sealed class SamplingServiceTests
{
    [Fact]
    public async Task RunAsync_WhenCancelledAfterCaptures_ThrowsIncompleteWithPartialSession()
    {
        var stage = new FakeStage();
        var camera = new FakeCamera { FailAfterCaptures = 2 };
        var sut = new SamplingService(stage, camera, NullLogger<SamplingService>.Instance);
        using var cts = new CancellationTokenSource();

        camera.OnCapture = count =>
        {
            if (count >= 2)
                cts.Cancel();
        };

        var ex = await Assert.ThrowsAsync<SamplingIncompleteException>(() =>
            sut.RunAsync(5, 0, 10, cancellationToken: cts.Token));

        Assert.Equal(2, ex.Session.Images.Count);
        Assert.Equal(5, ex.Session.RequestedCount);
        Assert.IsType<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task RunAsync_WhenCameraFailsAfterCaptures_ThrowsIncompleteWithPartialSession()
    {
        var stage = new FakeStage();
        var camera = new FakeCamera { ThrowOnCaptureIndex = 3 };
        var sut = new SamplingService(stage, camera, NullLogger<SamplingService>.Instance);

        var ex = await Assert.ThrowsAsync<SamplingIncompleteException>(() =>
            sut.RunAsync(5, 0, 10));

        Assert.Equal(2, ex.Session.Images.Count);
        Assert.Contains("boom", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenFailsBeforeAnyCapture_RethrowsOriginal()
    {
        var stage = new FakeStage { ThrowOnMove = true };
        var camera = new FakeCamera();
        var sut = new SamplingService(stage, camera, NullLogger<SamplingService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunAsync(3, 0, 10));
    }

    private sealed class FakeStage : IThorlabsClient
    {
        public bool ThrowOnMove { get; set; }
        public bool IsConnected => true;
        public bool IsMoving => false;
        public double PositionMm { get; private set; }
        public string Status => "fake";

        public Task ApplyMotionParametersAsync(double velocityMmPerSec, double accelerationMmPerSec2, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HomeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MoveAbsoluteAsync(double positionMm, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnMove)
                throw new InvalidOperationException("stage failed");
            PositionMm = positionMm;
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> ListDevices() => Array.Empty<string>();
    }

    private sealed class FakeCamera : ICameraService
    {
        private int _captures;
        public int? ThrowOnCaptureIndex { get; set; }
        public int FailAfterCaptures { get; set; } = int.MaxValue;
        public Action<int>? OnCapture { get; set; }

        public string Status => "fake";
        public bool IsConnected => true;
        public bool IsLive => true;
        public bool IsPreviewAvailable => true;
        public event EventHandler<CameraFrameEventArgs>? FrameReceived;

        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartLiveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopLiveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<byte[]> GetPreviewFrameAsync(CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 1 });

        public Task<byte[]> CaptureStillAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _captures++;

            if (ThrowOnCaptureIndex is int n && _captures == n)
                throw new InvalidOperationException("boom");

            var bytes = new byte[] { (byte)_captures };
            // Signal after a successful capture so the image is retained, then cancel hits the next loop check.
            OnCapture?.Invoke(_captures);
            return Task.FromResult(bytes);
        }
    }
}
