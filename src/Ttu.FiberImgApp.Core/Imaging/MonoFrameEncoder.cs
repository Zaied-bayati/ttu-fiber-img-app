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