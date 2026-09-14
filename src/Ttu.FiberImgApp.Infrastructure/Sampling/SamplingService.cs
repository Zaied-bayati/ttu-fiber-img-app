using Microsoft.Extensions.Logging;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Core.Sampling;

namespace Ttu.FiberImgApp.Infrastructure.Sampling;

public sealed class SamplingService : ISamplingService
{
    private readonly IThorlabsClient _stage;
    private readonly ICameraService _camera;
    private readonly ILogger<SamplingService> _logger;

    public SamplingService(
        IThorlabsClient stage,
        ICameraService camera,
        ILogger<SamplingService> logger)
    {
        _stage = stage;
        _camera = camera;
        _logger = logger;
    }

    public async Task<SamplingSession> RunAsync(
        int imageCount,
        double minMm,
        double maxMm,
        IProgress<SamplingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_stage.IsConnected)
        {
            throw new InvalidOperationException("Stage is not connected. Connect in Settings first.");
        }

        var positions = SamplingCalculator.ComputePositions(minMm, maxMm, imageCount);
        var session = new SamplingSession
        {
            MinMm = minMm,
            MaxMm = maxMm,
            RequestedCount = imageCount
        };

        for (var i = 0; i < positions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = positions[i];
            var imageNumber = i + 1;

            progress?.Report(new SamplingProgress
            {
                CurrentIndex = imageNumber,
                TotalCount = imageCount,
                PositionMm = position,
                Message = $"Moving to {position:0.###} mm (image {imageNumber} of {imageCount})…"
            });

            _logger.LogInformation("Sampling move to {Position} mm ({Index}/{Total})", position, imageNumber, imageCount);
            await _stage.MoveAbsoluteAsync(position, cancellationToken).ConfigureAwait(false);

            progress?.Report(new SamplingProgress
            {
                CurrentIndex = imageNumber,
                TotalCount = imageCount,
                PositionMm = position,
                Message = $"Capturing image {imageNumber} of {imageCount} at {position:0.###} mm…"
            });

            var png = await _camera.CaptureStillAsync(cancellationToken).ConfigureAwait(false);
            session.Images.Add(new CapturedImage
            {
                Index = imageNumber,
                PositionMm = position,
                CapturedAt = DateTimeOffset.Now,
                PngBytes = png,
                Keep = true
            });
        }

        progress?.Report(new SamplingProgress
        {
            CurrentIndex = imageCount,
            TotalCount = imageCount,
            PositionMm = _stage.PositionMm,
            Message = "Sampling complete."
        });

        return session;
    }
}
