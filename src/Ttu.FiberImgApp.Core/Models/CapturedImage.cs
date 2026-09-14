namespace Ttu.FiberImgApp.Core.Models;

public sealed class CapturedImage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Index { get; init; }
    public double PositionMm { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public byte[] PngBytes { get; set; } = Array.Empty<byte>();
    public bool Keep { get; set; } = true;
}