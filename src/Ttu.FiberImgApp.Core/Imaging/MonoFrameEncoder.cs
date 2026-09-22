using System.Buffers.Binary;
using System.IO.Compression;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Imaging;

/// <summary>
/// Converts mono / BGR camera frames to 8-bit grayscale PNG and BGRA for UI.
/// </summary>
public static class MonoFrameEncoder
{
    public static byte[] ToGray8(CameraFrame frame)
    {
        var pixels = frame.RawPixels;
        var count = checked(frame.Width * frame.Height);
        var gray = new byte[count];

        switch (frame.PixelFormat)
        {
            case CameraPixelFormat.Gray8:
                Buffer.BlockCopy(pixels, 0, gray, 0, Math.Min(pixels.Length, count));
                break;
            case CameraPixelFormat.Gray16:
                ScaleGray16To8(pixels, gray, count);
                break;
            case CameraPixelFormat.Bgr24:
                for (var i = 0; i < count; i++)
                {
                    var o = i * 3;
                    if (o + 2 >= pixels.Length) break;
                    // BT.601 luma from BGR
                    gray[i] = (byte)((pixels[o + 2] * 77 + pixels[o + 1] * 150 + pixels[o] * 29) >> 8);
                }
                break;
            case CameraPixelFormat.Bgr48:
                for (var i = 0; i < count; i++)
                {
                    var o = i * 6;
                    if (o + 5 >= pixels.Length) break;
                    var b = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(o));
                    var g = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(o + 2));
                    var r = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(o + 4));
                    gray[i] = (byte)(((r * 77 + g * 150 + b * 29) >> 8) >> 8);
                }
                break;
            default:
                throw new NotSupportedException($"Pixel format {frame.PixelFormat} is not supported.");
        }

        return gray;
    }

    public static byte[] ToBgra32(CameraFrame frame)
    {
        var gray = ToGray8(frame);
        return Gray8ToBgra32(gray);
    }

    /// <summary>
    /// Downsamples a frame for on-screen preview. Full-resolution Axiocam frames are tens of megabytes;
    /// expanding those to BGRA on the UI thread stalls the live view.
    /// </summary>
    public static PreviewBitmap ToPreviewBgra32(CameraFrame frame, int maxEdge = 1600)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame width and height must be positive.");
        if (frame.RawPixels.Length == 0)
            throw new InvalidOperationException("Frame has no pixel bytes.");

        var step = 1;
        var longEdge = Math.Max(frame.Width, frame.Height);
        if (maxEdge > 0 && longEdge > maxEdge)
            step = (int)Math.Ceiling(longEdge / (double)maxEdge);

        var width = Math.Max(1, frame.Width / step);
        var height = Math.Max(1, frame.Height / step);
        var gray = new byte[width * height];
        FillPreviewGray(frame, gray, width, height, step);
        return new PreviewBitmap(Gray8ToBgra32(gray), width, height);
    }

    public readonly record struct PreviewBitmap(byte[] Bgra, int Width, int Height);

    private static byte[] Gray8ToBgra32(byte[] gray)
    {
        var bgra = new byte[gray.Length * 4];
        for (var i = 0; i < gray.Length; i++)
        {
            var v = gray[i];
            var o = i * 4;
            bgra[o] = v;
            bgra[o + 1] = v;
            bgra[o + 2] = v;
            bgra[o + 3] = 255;
        }

        return bgra;
    }

    private static void FillPreviewGray(CameraFrame frame, byte[] gray, int width, int height, int step)
    {
        switch (frame.PixelFormat)
        {
            case CameraPixelFormat.Gray16:
                FillPreviewGray16(frame, gray, width, height, step);
                return;
            case CameraPixelFormat.Gray8:
            case CameraPixelFormat.Bgr24:
            case CameraPixelFormat.Bgr48:
                break;
            default:
                throw new NotSupportedException($"Pixel format {frame.PixelFormat} is not supported.");
        }

        for (var y = 0; y < height; y++)
        {
            var sy = y * step;
            for (var x = 0; x < width; x++)
            {
                var sx = x * step;
                gray[(y * width) + x] = SampleLuma8(frame, sx, sy);
            }
        }
    }

    private static void FillPreviewGray16(CameraFrame frame, byte[] gray, int width, int height, int step)
    {
        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;
        var samples = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            var sy = y * step;
            for (var x = 0; x < width; x++)
            {
                var sx = x * step;
                var v = ReadGray16(frame, sx, sy);
                samples[(y * width) + x] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (max <= min)
        {
            Array.Fill(gray, (byte)(min >> 8));
            return;
        }

        var range = max - min;
        for (var i = 0; i < samples.Length; i++)
            gray[i] = (byte)(((samples[i] - min) * 255) / range);
    }

    private static byte SampleLuma8(CameraFrame frame, int x, int y)
    {
        var pixels = frame.RawPixels;
        switch (frame.PixelFormat)
        {
            case CameraPixelFormat.Gray8:
            {
                var i = (y * frame.Width) + x;
                return i >= 0 && i < pixels.Length ? pixels[i] : (byte)0;
            }
            case CameraPixelFormat.Bgr24:
            {
                var o = ((y * frame.Width) + x) * 3;
                if (o < 0 || o + 2 >= pixels.Length) return 0;
                return (byte)((pixels[o + 2] * 77 + pixels[o + 1] * 150 + pixels[o] * 29) >> 8);
            }
            case CameraPixelFormat.Bgr48:
            {
                var o = ((y * frame.Width) + x) * 6;
                if (o < 0 || o + 5 >= pixels.Length) return 0;
                var b = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(o));
                var g = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(o + 2));
                var r = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(o + 4));
                return (byte)(((r * 77 + g * 150 + b * 29) >> 8) >> 8);
            }
            default:
                return 0;
        }
    }

    private static ushort ReadGray16(CameraFrame frame, int x, int y)
    {
        var o = ((y * frame.Width) + x) * 2;
        if (o < 0 || o + 1 >= frame.RawPixels.Length) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(frame.RawPixels.AsSpan(o));
    }

    public static byte[] ToPng(CameraFrame frame)
    {
        var gray = ToGray8(frame);
        return EncodeGrayPng(gray, frame.Width, frame.Height);
    }

    private static void ScaleGray16To8(byte[] src, byte[] dst, int count)
    {
        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;
        var needed = count * 2;
        var len = Math.Min(src.Length, needed);
        for (var i = 0; i + 1 < len; i += 2)
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(i));
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (max <= min)
        {
            Array.Fill(dst, (byte)(min >> 8));
            return;
        }

        var range = max - min;
        for (var i = 0; i < count; i++)
        {
            var o = i * 2;
            if (o + 1 >= src.Length) break;
            var v = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(o));
            dst[i] = (byte)(((v - min) * 255) / range);
        }
    }

    private static byte[] EncodeGrayPng(byte[] gray, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 0; // grayscale
        WriteChunk(ms, "IHDR"u8, ihdr);

        var stride = 1 + width;
        var raw = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            raw[row] = 0;
            Buffer.BlockCopy(gray, y * width, raw, row + 1, width);
        }

        WriteChunk(ms, "IDAT"u8, ZlibCompress(raw));
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        // zlib header
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        var adler = Adler32(raw);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, adler);
        ms.Write(checksum);
        return ms.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);
        stream.Write(type);
        stream.Write(data);

        var crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var x in type) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}