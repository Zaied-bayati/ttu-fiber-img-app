namespace Ttu.FiberImgApp.Core.Models;

/// <summary>What the ZEN pixel stream actually delivered during a short unfiltered listen.</summary>
public sealed class ZenStreamProbeResult
{
    public int TotalMessages { get; init; }
    public int PixelMessages { get; init; }

    /// <summary>Distinct channel indexes (frame_position.c) seen on messages that carried pixels.</summary>
    public IReadOnlyList<int> Channels { get; init; } = [];

    /// <summary>Plain-language explanation, safe to show directly in the UI.</summary>
    public string Summary { get; init; } = string.Empty;
}
