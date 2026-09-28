namespace Ttu.FiberImgApp.Core.Models;

/// <summary>
/// One acquisition. PreviewPng is for the UI. CziPath and TiffPath are full-quality files on disk.
/// </summary>
public sealed class CameraCapture
{
    public required byte[] PreviewPng { get; init; }
    public string? CziPath { get; init; }
    public string? TiffPath { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? PixelFormat { get; init; }
    public int? Channel { get; init; }
}
