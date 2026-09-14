namespace Ttu.FiberImgApp.Core.Models;

public sealed class SamplingProgress
{
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public double PositionMm { get; init; }
    public string Message { get; init; } = string.Empty;
}