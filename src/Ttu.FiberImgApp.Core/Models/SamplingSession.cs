namespace Ttu.FiberImgApp.Core.Models;

public sealed class SamplingSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public double MinMm { get; init; }
    public double MaxMm { get; init; }
    public int RequestedCount { get; init; }
    public List<CapturedImage> Images { get; } = new();
}