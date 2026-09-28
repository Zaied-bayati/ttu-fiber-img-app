namespace Ttu.FiberImgApp.Core.Models;

public sealed class CapturedImage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Index { get; init; }
    public double PositionMm { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public byte[] PngBytes { get; set; } = Array.Empty<byte>();

    /// <summary>ZEN-written CZI. This is the master image.</summary>
    public string? CziPath { get; set; }

    /// <summary>Uncompressed grayscale TIFF of the original samples, when the frame was received.</summary>
    public string? TiffPath { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public string? PixelFormat { get; set; }
    public bool Keep { get; set; } = true;
}