using System.Buffers.Binary;
using System.Text;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Imaging;

/// <summary>
/// Minimal reader for Zeiss CZI (ZISRAW) files: finds the first image plane and returns its pixels so a
/// capture can be previewed, exported to PNG/TIFF/BMP, and used for live view without the pixel stream.
/// Layout: 32-byte segment headers (id[16], allocated size, used size); file header segment holds the
/// directory position; directory entries ("DV") describe each sub-block (pixel type, file position,
/// compression, dimensions); a sub-block's pixel data follows its 256-byte-minimum fixed part.
/// </summary>
public static class CziReader
{
    private const int SegmentHeaderSize = 32;
    private const long MaxCanvasBytes = 1_500_000_000;

    public static int BytesPerPixel(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Gray8 => 1,
        CameraPixelFormat.Gray16 => 2,
        CameraPixelFormat.Bgr24 => 3,
        CameraPixelFormat.Bgr48 => 6,
        _ => 0,
    };

    /// <param name="permanent">
    /// True when the file was readable but cannot be used (unsupported compression or pixel type), so retrying
    /// will not help. False for problems that can clear up, such as ZEN still writing the file.
    /// </param>
    public static bool TryReadFirstPlane(
        string path,
        IReadOnlyList<ICziTileDecoder>? extraDecoders,
        out CameraFrame? frame,
        out string error,
        out bool permanent)
    {
        frame = null;
        permanent = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryReadFirstPlane(stream, extraDecoders, out frame, out error, out permanent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            error = $"could not read {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    public static bool TryReadFirstPlane(
        Stream stream,
        IReadOnlyList<ICziTileDecoder>? extraDecoders,
        out CameraFrame? frame,
        out string error,
        out bool permanent) =>
        TryReadPlane(stream, extraDecoders, latest: false, afterKey: -1, out frame, out _, out error, out permanent);

    /// <summary>
    /// Reads one plane. With <paramref name="latest"/> it is the most recently written one (the live view follows a
    /// file ZEN is still appending to); a plane whose key is not past <paramref name="afterKey"/> is not read again and
    /// the call succeeds with a null frame. <paramref name="key"/> identifies the plane (its file position).
    /// </summary>
    internal static bool TryReadPlane(
        Stream stream,
        IReadOnlyList<ICziTileDecoder>? extraDecoders,
        bool latest,
        long afterKey,
        out CameraFrame? frame,
        out long key,
        out string error,
        out bool permanent)
    {
        frame = null;
        key = -1;
        permanent = false;
        try
        {
            if (!ReadSegmentHeader(stream, 0, out var id, out var allocated, out _) || id != "ZISRAWFILE")
            {
                error = "not a CZI file (missing ZISRAWFILE header)";
                return false;
            }

            var header = new byte[80];
            stream.Position = SegmentHeaderSize;
            stream.ReadExactly(header, 0, (int)Math.Min(header.Length, Math.Max(allocated, 0)));
            var directoryPosition = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(52));

            var entries = directoryPosition > 0 ? ReadDirectory(stream, directoryPosition) : [];
            if (entries.Count == 0)
                entries = ScanSubBlocks(stream, SegmentHeaderSize + allocated);
            if (entries.Count == 0)
            {
                error = "the CZI contains no image sub-blocks";
                return false;
            }

            var metadataPosition = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(60));
            var assembled = TryAssemblePlane(
                stream, entries, extraDecoders, metadataPosition, latest, afterKey, out frame, out key, out error, out var unusable);
            permanent = unusable;
            return assembled;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or OverflowException or ArgumentException)
        {
            error = $"CZI structure not understood: {ex.Message}";
            return false;
        }
    }

    private static bool TryAssemblePlane(
        Stream stream,
        List<Entry> allEntries,
        IReadOnlyList<ICziTileDecoder>? extraDecoders,
        long metadataPosition,
        bool latest,
        long afterKey,
        out CameraFrame? frame,
        out long key,
        out string error,
        out bool unusable)
    {
        frame = null;
        key = -1;
        unusable = true; // every failure below means the data was readable but cannot be turned into an image

        var candidates = allEntries.Where(e => e.PyramidType == 0 && e.Has('X') && e.Has('Y')).ToList();
        if (candidates.Count == 0)
            candidates = allEntries.Where(e => e.Has('X') && e.Has('Y')).ToList();
        if (candidates.Count == 0)
        {
            error = "the CZI has no X/Y image sub-blocks";
            return false;
        }

        Entry first;
        if (latest)
        {
            // The newest write of the lowest channel: planes are appended in acquisition order.
            var channel = candidates.Min(e => e.Start('C'));
            first = candidates.Where(e => e.Start('C') == channel).OrderByDescending(e => e.FilePosition).First();
            key = first.FilePosition;
            if (key <= afterKey)
            {
                error = string.Empty;
                unusable = false;
                return true; // nothing newer than the plane already shown
            }
        }
        else
        {
            first = candidates
                .OrderBy(e => e.Start('C')).ThenBy(e => e.Start('Z')).ThenBy(e => e.Start('T')).ThenBy(e => e.Start('S'))
                .First();
            key = first.FilePosition;
        }

        var plane = candidates.Where(e => e.SamePlaneAs(first)).ToList();

        var format = first.PixelType switch
        {
            0 => CameraPixelFormat.Gray8,
            1 => CameraPixelFormat.Gray16,
            3 => CameraPixelFormat.Bgr24,
            4 => CameraPixelFormat.Bgr48,
            _ => (CameraPixelFormat)0,
        };
        var bytesPerPixel = BytesPerPixel(format);
        if (bytesPerPixel == 0)
        {
            error = $"CZI pixel type {first.PixelType} is not supported (Gray8, Gray16, Bgr24, Bgr48 are)";
            return false;
        }

        var minX = plane.Min(e => e.Start('X'));
        var minY = plane.Min(e => e.Start('Y'));
        var maxX = plane.Max(e => e.Start('X') + e.Stored('X'));
        var maxY = plane.Max(e => e.Start('Y') + e.Stored('Y'));
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 0 || height <= 0 || (long)width * height * bytesPerPixel > MaxCanvasBytes)
        {
            error = $"CZI plane size {width}x{height} is not usable";
            return false;
        }

        var decoders = new List<ICziTileDecoder>();
        if (extraDecoders is not null)
            decoders.AddRange(extraDecoders);
        decoders.Add(new BuiltInCziTileDecoder());

        var canvas = new byte[width * height * bytesPerPixel];
        foreach (var tile in plane)
        {
            var tileWidth = tile.Stored('X');
            var tileHeight = tile.Stored('Y');
            var data = ReadSubBlockData(stream, tile);

            var decoder = decoders.FirstOrDefault(d => d.CanDecode(tile.Compression));
            if (decoder is null)
            {
                error = $"CZI tile uses {CompressionName(tile.Compression)} compression, which this app cannot decode yet";
                return false;
            }

            if (!decoder.TryDecode(tile.Compression, data, tileWidth, tileHeight, format, out var pixels, out var decodeError))
            {
                error = $"CZI tile ({CompressionName(tile.Compression)}) could not be decoded: {decodeError}";
                return false;
            }

            var rowBytes = tileWidth * bytesPerPixel;
            for (var row = 0; row < tileHeight; row++)
            {
                var destination = (((tile.Start('Y') - minY + row) * width) + (tile.Start('X') - minX)) * bytesPerPixel;
                Buffer.BlockCopy(pixels, row * rowBytes, canvas, destination, rowBytes);
            }
        }

        frame = new CameraFrame
        {
            RawPixels = canvas,
            Width = width,
            Height = height,
            PixelFormat = format,
            CapturedAt = DateTimeOffset.UtcNow,
            SignificantBits = format == CameraPixelFormat.Gray16 ? TryReadComponentBitCount(stream, metadataPosition) : null,
        };
        error = string.Empty;
        unusable = false;
        return true;
    }

    /// <summary>
    /// Best effort: reads the sensor bit depth (Information/Image/ComponentBitCount) from the metadata XML.
    /// The metadata segment's data holds XmlSize, AttachmentSize, then the XML starting 256 bytes in.
    /// </summary>
    private static int? TryReadComponentBitCount(Stream stream, long metadataPosition)
    {
        try
        {
            if (!ReadSegmentHeader(stream, metadataPosition, out var id, out var allocated, out _) || id != "ZISRAWMETADATA")
                return null;

            var sizes = new byte[8];
            stream.ReadExactly(sizes);
            var xmlSize = BinaryPrimitives.ReadInt32LittleEndian(sizes);
            if (xmlSize <= 0 || xmlSize > 32_000_000 || 256 + xmlSize > allocated)
                return null;

            stream.Position = metadataPosition + SegmentHeaderSize + 256;
            var xml = new byte[xmlSize];
            stream.ReadExactly(xml);

            var match = System.Text.RegularExpressions.Regex.Match(
                Encoding.UTF8.GetString(xml),
                @"<ComponentBitCount>\s*(\d+)\s*</ComponentBitCount>");
            return match.Success && int.TryParse(match.Groups[1].Value, out var bits) && bits is >= 8 and <= 16 ? bits : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string CompressionName(int compression) => compression switch
    {
        0 => "uncompressed",
        1 => "JPEG",
        2 => "LZW",
        4 => "JPEG XR",
        5 => "Zstandard",
        6 => "Zstandard (hi/lo)",
        _ => $"mode {compression}",
    };

    private static List<Entry> ReadDirectory(Stream stream, long position)
    {
        if (!ReadSegmentHeader(stream, position, out var id, out var allocated, out var used) || id != "ZISRAWDIRECTORY")
            return [];

        var length = (int)Math.Min(Math.Max(used, 0), Math.Min(allocated, 256_000_000));
        if (length < 128)
            return [];

        var data = new byte[length];
        stream.ReadExactly(data, 0, length);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        var entries = new List<Entry>(Math.Max(count, 0));
        var offset = 128; // 4 bytes entry count + 124 reserved
        for (var i = 0; i < count && offset + 32 <= length; i++)
        {
            var entry = ParseEntry(data, offset, filePositionOverride: null);
            if (entry is null)
                break;
            entries.Add(entry);
            offset += entry.EntrySize;
        }

        return entries;
    }

    // Used only when the directory is missing or unreadable: walk the segments and read each sub-block's own copy of its entry.
    private static List<Entry> ScanSubBlocks(Stream stream, long start)
    {
        var entries = new List<Entry>();
        var position = start;
        for (var guard = 0; guard < 200_000 && position + SegmentHeaderSize <= stream.Length; guard++)
        {
            if (!ReadSegmentHeader(stream, position, out var id, out var allocated, out var used))
                break;

            // A segment that runs past the end of the file is still being written; leave it for the next look.
            if (position + SegmentHeaderSize + Math.Max(used, 0) > stream.Length)
                break;

            if (id == "ZISRAWSUBBLOCK")
            {
                var length = (int)Math.Min(Math.Max(used, 0), 2048);
                if (length > 48)
                {
                    var data = new byte[length];
                    stream.ReadExactly(data, 0, length);
                    var entry = ParseEntry(data, 16, filePositionOverride: position);
                    if (entry is not null)
                        entries.Add(entry);
                }
            }
            else if (id.Length == 0 || !id.StartsWith("ZISRAW", StringComparison.Ordinal))
            {
                break;
            }

            if (allocated <= 0)
                break;
            position += SegmentHeaderSize + allocated;
        }

        return entries;
    }

    private static Entry? ParseEntry(byte[] data, int offset, long? filePositionOverride)
    {
        if (offset + 32 > data.Length || data[offset] != (byte)'D' || data[offset + 1] != (byte)'V')
            return null;

        var span = data.AsSpan(offset);
        var pixelType = BinaryPrimitives.ReadInt32LittleEndian(span[2..]);
        var filePosition = BinaryPrimitives.ReadInt64LittleEndian(span[6..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(span[18..]);
        var pyramidType = span[22];
        var dimensionCount = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
        if (dimensionCount is < 0 or > 64 || offset + 32 + (dimensionCount * 20) > data.Length)
            return null;

        var dimensions = new Dimension[dimensionCount];
        for (var i = 0; i < dimensionCount; i++)
        {
            var d = span[(32 + (i * 20))..];
            dimensions[i] = new Dimension(
                (char)d[0],
                BinaryPrimitives.ReadInt32LittleEndian(d[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[16..]));
        }

        return new Entry(pixelType, filePositionOverride ?? filePosition, compression, pyramidType, dimensions, 32 + (dimensionCount * 20));
    }

    private static byte[] ReadSubBlockData(Stream stream, Entry entry)
    {
        if (!ReadSegmentHeader(stream, entry.FilePosition, out var id, out var allocated, out _) || id != "ZISRAWSUBBLOCK")
            throw new InvalidDataException($"no sub-block at file position {entry.FilePosition}");

        var dataStart = entry.FilePosition + SegmentHeaderSize;
        var sizes = new byte[16];
        stream.Position = dataStart;
        stream.ReadExactly(sizes);
        var metadataSize = BinaryPrimitives.ReadInt32LittleEndian(sizes);
        var dataSize = BinaryPrimitives.ReadInt64LittleEndian(sizes.AsSpan(8));

        // The fixed part (sizes + directory entry + fill) is at least 256 bytes.
        var fixedPart = Math.Max(256, 16 + entry.EntrySize);
        if (metadataSize < 0 || dataSize <= 0 || dataSize > int.MaxValue
            || fixedPart + metadataSize + dataSize > allocated)
            throw new InvalidDataException("sub-block sizes are inconsistent");

        stream.Position = dataStart + fixedPart + metadataSize;
        var data = new byte[dataSize];
        stream.ReadExactly(data);
        return data;
    }

    private static bool ReadSegmentHeader(Stream stream, long position, out string id, out long allocated, out long used)
    {
        id = string.Empty;
        allocated = 0;
        used = 0;
        if (position < 0 || position + SegmentHeaderSize > stream.Length)
            return false;

        var header = new byte[SegmentHeaderSize];
        stream.Position = position;
        stream.ReadExactly(header);
        id = Encoding.ASCII.GetString(header, 0, 16).TrimEnd('\0');
        allocated = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        used = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
        return true;
    }

    private readonly record struct Dimension(char Id, int Start, int Size, int StoredSize);

    private sealed record Entry(
        int PixelType,
        long FilePosition,
        int Compression,
        byte PyramidType,
        Dimension[] Dimensions,
        int EntrySize)
    {
        public bool Has(char id) => Dimensions.Any(d => d.Id == id);

        public int Start(char id) => Dimensions.FirstOrDefault(d => d.Id == id).Start;

        public int Stored(char id)
        {
            var d = Dimensions.FirstOrDefault(x => x.Id == id);
            return d.StoredSize > 0 ? d.StoredSize : d.Size;
        }

        // Same non-spatial coordinates (channel, z, time, scene, ...) means the same plane; X/Y/M just position tiles.
        public bool SamePlaneAs(Entry other) =>
            Dimensions.Where(d => d.Id is not ('X' or 'Y' or 'M')).All(d => other.Start(d.Id) == d.Start)
            && other.Dimensions.Where(d => d.Id is not ('X' or 'Y' or 'M')).All(d => Start(d.Id) == d.Start);
    }
}
