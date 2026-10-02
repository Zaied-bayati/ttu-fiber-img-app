using System.Buffers.Binary;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Imaging;

/// <summary>
/// Writes an uncompressed 8-bit indexed grayscale BMP for AI-training pipelines. Gray16 samples are scaled by
/// the sensor's bit depth (a fixed, frame-independent mapping) rather than per-frame min/max stretched, so the
/// same physical intensity maps to the same byte value across an image set. The .tif / .czi remain the
/// lossless masters; this is an intentionally lossy, 8-bit companion.
/// </summary>
public static class MonoBmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PaletteEntries = 256;
    private const int PaletteSize = PaletteEntries * 4;
    private const int PixelDataOffset = FileHeaderSize + InfoHeaderSize + PaletteSize;

    public static void Write(string path, CameraFrame frame)
    {
        byte[] gray8 = frame.PixelFormat switch
        {
            CameraPixelFormat.Gray8 => frame.RawPixels,
            CameraPixelFormat.Gray16 => ScaleGray16To8(frame.RawPixels, frame.Width * frame.Height, frame.SignificantBits),
            _ => throw new NotSupportedException($"BMP export does not support {frame.PixelFormat}. The CZI from ZEN keeps that data."),
        };

        var pixelCount = checked(frame.Width * frame.Height);
        if (gray8.Length < pixelCount)
            throw new InvalidOperationException($"Need {pixelCount} gray samples for {frame.Width}x{frame.Height}, got {gray8.Length}.");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stride = (frame.Width + 3) / 4 * 4;
        var imageSize = stride * frame.Height;
        var fileSize = PixelDataOffset + imageSize;

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        Span<byte> fileHeader = stackalloc byte[FileHeaderSize];
        fileHeader[0] = (byte)'B';
        fileHeader[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader[2..], (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader[10..], PixelDataOffset);
        stream.Write(fileHeader);

        Span<byte> infoHeader = stackalloc byte[InfoHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(infoHeader[0..], InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(infoHeader[4..], frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(infoHeader[8..], frame.Height); // positive => bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(infoHeader[12..], 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(infoHeader[14..], 8); // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(infoHeader[16..], 0); // BI_RGB, no compression
        BinaryPrimitives.WriteUInt32LittleEndian(infoHeader[20..], (uint)imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(infoHeader[24..], 2835); // ~72 DPI
        BinaryPrimitives.WriteInt32LittleEndian(infoHeader[28..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(infoHeader[32..], PaletteEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(infoHeader[36..], 0);
        stream.Write(infoHeader);

        Span<byte> paletteEntry = stackalloc byte[4];
        for (var i = 0; i < PaletteEntries; i++)
        {
            paletteEntry[0] = (byte)i;
            paletteEntry[1] = (byte)i;
            paletteEntry[2] = (byte)i;
            paletteEntry[3] = 0;
            stream.Write(paletteEntry);
        }

        var row = new byte[stride];
        for (var y = frame.Height - 1; y >= 0; y--) // bottom-up
        {
            Array.Clear(row, 0, row.Length);
            Array.Copy(gray8, y * frame.Width, row, 0, frame.Width);
            stream.Write(row, 0, stride);
        }
    }

    /// <summary>
    /// Drops the unused low bits: shift = bitDepth - 8. The depth comes from the sensor (CZI ComponentBitCount) so
    /// every image from the same camera is scaled identically; only when it is unknown is it inferred from this
    /// frame's brightest sample.
    /// </summary>
    private static byte[] ScaleGray16To8(byte[] src, int sampleCount, int? significantBits)
    {
        var count = Math.Min(sampleCount, src.Length / 2);
        int bits;
        if (significantBits is >= 8 and <= 16)
        {
            bits = significantBits.Value;
        }
        else
        {
            ushort max = 0;
            for (var i = 0; i < count; i++)
            {
                var v = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(i * 2));
                if (v > max) max = v;
            }

            bits = Math.Clamp(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max), 8, 16);
        }

        var shift = bits - 8;
        var dst = new byte[sampleCount];
        for (var i = 0; i < count; i++)
            dst[i] = (byte)Math.Min(255, BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(i * 2)) >> shift);

        return dst;
    }
}
