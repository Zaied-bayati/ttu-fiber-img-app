using System.Buffers.Binary;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Imaging;

/// <summary>
/// Writes uncompressed grayscale TIFF without stretching pixel values.
/// </summary>
public static class MonoTiffWriter
{
    public static void Write(string path, CameraFrame frame)
    {
        var bits = frame.PixelFormat switch
        {
            CameraPixelFormat.Gray8 => 8,
            CameraPixelFormat.Gray16 => 16,
            _ => throw new NotSupportedException($"TIFF export does not support {frame.PixelFormat}. The CZI from ZEN keeps that data."),
        };

        var bytesPerSample = bits / 8;
        var pixelBytes = checked(frame.Width * frame.Height * bytesPerSample);
        if (frame.RawPixels.Length < pixelBytes)
            throw new InvalidOperationException($"Need {pixelBytes} bytes for {frame.Width}x{frame.Height} {frame.PixelFormat}, got {frame.RawPixels.Length}.");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        const int pixelsAt = 8;
        var ifdAt = pixelsAt + pixelBytes;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[8];
        header[0] = (byte)'I';
        header[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 42);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], ifdAt);
        stream.Write(header);
        stream.Write(frame.RawPixels, 0, pixelBytes);

        // Tags must be ascending. SHORT values sit in the low two bytes.
        Span<(ushort Tag, ushort Type, uint Count, uint Value)> entries =
        [
            (256, 4, 1, (uint)frame.Width),
            (257, 4, 1, (uint)frame.Height),
            (258, 3, 1, (uint)bits),
            (259, 3, 1, 1),
            (262, 3, 1, 1),
            (273, 4, 1, (uint)pixelsAt),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)frame.Height),
            (279, 4, 1, (uint)pixelBytes),
            (339, 3, 1, 1),
        ];

        Span<byte> count = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)entries.Length);
        stream.Write(count);

        Span<byte> entry = stackalloc byte[12];
        foreach (var item in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, item.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], item.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], item.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], item.Value);
            stream.Write(entry);
        }

        Span<byte> next = stackalloc byte[4];
        stream.Write(next);
    }
}
