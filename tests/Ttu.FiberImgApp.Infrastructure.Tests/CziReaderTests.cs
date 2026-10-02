using System.Buffers.Binary;
using System.Text;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;
using ZstdSharp;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public class CziReaderTests
{
    [Fact]
    public void Uncompressed_gray16_plane_round_trips()
    {
        var pixels = Gray16Bytes(4, 3);
        var czi = CziFile.Build([new CziFile.Tile(PixelType: 1, Compression: 0, X: 0, Y: 0, Width: 4, Height: 3, Data: pixels)]);

        var ok = Read(czi, out var frame, out var error, out _);

        Assert.True(ok, error);
        Assert.Equal(4, frame!.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(CameraPixelFormat.Gray16, frame.PixelFormat);
        Assert.Equal(pixels, frame.RawPixels);
    }

    [Fact]
    public void Sensor_bit_depth_is_read_from_the_metadata_xml()
    {
        var czi = CziFile.Build(
            [new CziFile.Tile(1, 0, 0, 0, 2, 2, Gray16Bytes(2, 2))],
            metadataXml: "<ImageDocument><Metadata><Information><Image><ComponentBitCount>14</ComponentBitCount></Image></Information></Metadata></ImageDocument>");

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(14, frame!.SignificantBits);
    }

    [Fact]
    public void Missing_metadata_leaves_the_bit_depth_unknown_without_failing_the_read()
    {
        var czi = CziFile.Build([new CziFile.Tile(1, 0, 0, 0, 2, 2, Gray16Bytes(2, 2))]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Null(frame!.SignificantBits);
    }

    [Fact]
    public void Gray8_plane_round_trips()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6];
        var czi = CziFile.Build([new CziFile.Tile(0, 0, 0, 0, 3, 2, pixels)]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(CameraPixelFormat.Gray8, frame!.PixelFormat);
        Assert.Equal(pixels, frame.RawPixels);
    }

    [Fact]
    public void Mosaic_tiles_are_placed_by_their_x_y_start()
    {
        byte[] left = [1, 1, 2, 2];   // 2x2
        byte[] right = [3, 3, 4, 4];  // 2x2, starts at x=2
        var czi = CziFile.Build(
        [
            new CziFile.Tile(0, 0, 0, 0, 2, 2, left),
            new CziFile.Tile(0, 0, 2, 0, 2, 2, right),
        ]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(4, frame!.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(new byte[] { 1, 1, 3, 3, 2, 2, 4, 4 }, frame.RawPixels);
    }

    [Fact]
    public void Only_the_first_channel_plane_is_returned()
    {
        byte[] channel0 = [10, 11, 12, 13];
        byte[] channel1 = [90, 91, 92, 93];
        var czi = CziFile.Build(
        [
            new CziFile.Tile(0, 0, 0, 0, 2, 2, channel1, Channel: 1),
            new CziFile.Tile(0, 0, 0, 0, 2, 2, channel0, Channel: 0),
        ]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(channel0, frame!.RawPixels);
    }

    [Fact]
    public void Pyramid_levels_are_ignored_in_favor_of_full_resolution()
    {
        byte[] full = [1, 2, 3, 4];
        byte[] thumbnail = [9];
        var czi = CziFile.Build(
        [
            new CziFile.Tile(0, 0, 0, 0, 1, 1, thumbnail, Pyramid: 1),
            new CziFile.Tile(0, 0, 0, 0, 2, 2, full),
        ]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(2, frame!.Width);
        Assert.Equal(full, frame.RawPixels);
    }

    [Fact]
    public void Zstandard_compressed_plane_is_decoded()
    {
        var pixels = Gray16Bytes(8, 8);
        using var compressor = new Compressor();
        var payload = compressor.Wrap(pixels).ToArray();
        var czi = CziFile.Build([new CziFile.Tile(1, Compression: 5, 0, 0, 8, 8, payload)]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(pixels, frame!.RawPixels);
    }

    [Fact]
    public void Zstandard1_with_hi_lo_packing_is_unpacked()
    {
        var pixels = Gray16Bytes(4, 4);
        var count = pixels.Length / 2;
        var packed = new byte[pixels.Length];
        for (var i = 0; i < count; i++)
        {
            packed[i] = pixels[2 * i];              // low bytes first
            packed[count + i] = pixels[(2 * i) + 1]; // then high bytes
        }

        using var compressor = new Compressor();
        var zstd = compressor.Wrap(packed).ToArray();
        byte[] header = [3, 1, 1]; // size 3, chunk type 1 (hi/lo), value 1
        var payload = header.Concat(zstd).ToArray();
        var czi = CziFile.Build([new CziFile.Tile(1, Compression: 6, 0, 0, 4, 4, payload)]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(pixels, frame!.RawPixels);
    }

    [Fact]
    public void Unsupported_compression_is_reported_as_permanent_and_named()
    {
        var czi = CziFile.Build([new CziFile.Tile(1, Compression: 4, 0, 0, 2, 2, new byte[16])]);

        var ok = Read(czi, out var frame, out var error, out var permanent);

        Assert.False(ok);
        Assert.Null(frame);
        Assert.True(permanent);
        Assert.Contains("JPEG XR", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_extra_decoder_is_used_for_modes_the_reader_does_not_handle()
    {
        byte[] decoded = [7, 7, 7, 7, 7, 7, 7, 7];
        var czi = CziFile.Build([new CziFile.Tile(1, Compression: 4, 0, 0, 2, 2, new byte[] { 1, 2, 3 })]);
        using var stream = new MemoryStream(czi);

        var ok = CziReader.TryReadFirstPlane(stream, [new FakeJxrDecoder(decoded)], out var frame, out var error, out _);

        Assert.True(ok, error);
        Assert.Equal(decoded, frame!.RawPixels);
    }

    [Fact]
    public void A_missing_directory_falls_back_to_scanning_the_sub_blocks()
    {
        var pixels = Gray16Bytes(3, 3);
        var czi = CziFile.Build([new CziFile.Tile(1, 0, 0, 0, 3, 3, pixels)], includeDirectory: false);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(pixels, frame!.RawPixels);
    }

    [Fact]
    public void A_file_that_is_not_a_czi_is_rejected_without_throwing()
    {
        var ok = Read(Encoding.ASCII.GetBytes("this is definitely not a czi file, just some text padding it out"), out _, out var error, out _);

        Assert.False(ok);
        Assert.Contains("not a CZI", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_file_is_a_retryable_failure_not_a_permanent_one()
    {
        var full = CziFile.Build([new CziFile.Tile(1, 0, 0, 0, 4, 4, Gray16Bytes(4, 4))]);
        var truncated = full[..(full.Length - 20)];

        var ok = Read(truncated, out _, out _, out var permanent);

        Assert.False(ok);
        Assert.False(permanent);
    }

    [Fact]
    public void Reading_from_a_path_works_while_another_handle_has_the_file_open()
    {
        var pixels = Gray16Bytes(5, 2);
        var path = Path.Combine(Path.GetTempPath(), "fiberimg-czi-tests", Guid.NewGuid().ToString("N") + ".czi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CziFile.Build([new CziFile.Tile(1, 0, 0, 0, 5, 2, pixels)]));
        try
        {
            using var writerHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            Assert.True(CziReader.TryReadFirstPlane(path, null, out var frame, out var error, out _), error);
            Assert.Equal(pixels, frame!.RawPixels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void First_plane_of_a_time_series_is_the_earliest_time_point()
    {
        byte[] t0 = [1, 1, 1, 1];
        byte[] t1 = [2, 2, 2, 2];
        var czi = CziFile.Build([new CziFile.Tile(0, 0, 0, 0, 2, 2, t1, Time: 1), new CziFile.Tile(0, 0, 0, 0, 2, 2, t0, Time: 0)]);

        Assert.True(Read(czi, out var frame, out var error, out _), error);
        Assert.Equal(t0, frame!.RawPixels);
    }

    [Fact]
    public void Tail_returns_the_newest_plane_of_a_finished_time_series_and_nothing_twice()
    {
        byte[] t0 = [1, 1, 1, 1];
        byte[] t1 = [2, 2, 2, 2];
        byte[] t2 = [3, 3, 3, 3];
        using var file = TempCzi.Write(CziFile.Build(
        [
            new CziFile.Tile(0, 0, 0, 0, 2, 2, t0, Time: 0),
            new CziFile.Tile(0, 0, 0, 0, 2, 2, t1, Time: 1),
            new CziFile.Tile(0, 0, 0, 0, 2, 2, t2, Time: 2),
        ]));
        var tail = new CziTail();

        Assert.True(tail.TryReadNew(file.Path, null, force: false, out var first, out var error, out _), error);
        Assert.Equal(t2, first!.RawPixels);

        Assert.True(tail.TryReadNew(file.Path, null, force: true, out var again, out error, out _), error);
        Assert.Null(again);
    }

    [Fact]
    public void Tail_follows_a_file_that_is_still_growing_and_ignores_the_half_written_plane()
    {
        byte[] t0 = [1, 1, 1, 1];
        byte[] t1 = [2, 2, 2, 2];
        // No directory yet, as in a file ZEN has not finished: the reader has to walk the sub-blocks.
        var oneDone = CziFile.Build([new CziFile.Tile(0, 0, 0, 0, 2, 2, t0, Time: 0)], includeDirectory: false);
        var twoDone = CziFile.Build(
            [new CziFile.Tile(0, 0, 0, 0, 2, 2, t0, Time: 0), new CziFile.Tile(0, 0, 0, 0, 2, 2, t1, Time: 1)],
            includeDirectory: false);
        var halfWritten = twoDone[..(twoDone.Length - 10)];

        using var file = TempCzi.Write(oneDone);
        var tail = new CziTail();

        Assert.True(tail.TryReadNew(file.Path, null, false, out var frame, out var error, out _), error);
        Assert.Equal(t0, frame!.RawPixels);

        file.Overwrite(halfWritten);
        Assert.True(tail.TryReadNew(file.Path, null, false, out frame, out error, out _), error);
        Assert.Null(frame); // the second plane is not complete, so nothing new yet

        file.Overwrite(twoDone);
        Assert.True(tail.TryReadNew(file.Path, null, false, out frame, out error, out _), error);
        Assert.Equal(t1, frame!.RawPixels);
    }

    [Fact]
    public void Tail_does_not_reread_a_file_that_has_not_grown()
    {
        using var file = TempCzi.Write(CziFile.Build([new CziFile.Tile(0, 0, 0, 0, 2, 2, [1, 2, 3, 4])], includeDirectory: false));
        var tail = new CziTail();
        Assert.True(tail.TryReadNew(file.Path, null, false, out var frame, out var error, out _), error);
        Assert.NotNull(frame);

        // Same bytes, same length: the cheap check answers without scanning.
        Assert.True(tail.TryReadNew(file.Path, null, false, out frame, out error, out _), error);
        Assert.Null(frame);
        Assert.True(tail.FileLength > 0);
    }

    [Fact]
    public void Tail_reports_a_missing_file_as_retryable()
    {
        var tail = new CziTail();

        var ok = tail.TryReadNew(Path.Combine(Path.GetTempPath(), "fiberimg-missing-" + Guid.NewGuid().ToString("N") + ".czi"), null, false, out var frame, out _, out var permanent);

        Assert.False(ok);
        Assert.Null(frame);
        Assert.False(permanent);
    }

    [Fact]
    public void Tail_reports_an_unsupported_compression_as_permanent()
    {
        using var file = TempCzi.Write(CziFile.Build([new CziFile.Tile(1, Compression: 4, 0, 0, 2, 2, new byte[16])]));
        var tail = new CziTail();

        var ok = tail.TryReadNew(file.Path, null, false, out _, out var error, out var permanent);

        Assert.False(ok);
        Assert.True(permanent);
        Assert.Contains("JPEG XR", error, StringComparison.Ordinal);
    }

    private sealed class TempCzi : IDisposable
    {
        private TempCzi(string path) => Path = path;

        public string Path { get; }

        public static TempCzi Write(byte[] bytes)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fiberimg-czi-tests", Guid.NewGuid().ToString("N") + ".czi");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return new TempCzi(path);
        }

        public void Overwrite(byte[] bytes) => File.WriteAllBytes(Path, bytes);

        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { }
        }
    }

    private static bool Read(byte[] czi, out CameraFrame? frame, out string error, out bool permanent)
    {
        using var stream = new MemoryStream(czi);
        return CziReader.TryReadFirstPlane(stream, null, out frame, out error, out permanent);
    }

    private static byte[] Gray16Bytes(int width, int height)
    {
        var bytes = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)(1000 + (i * 37)));
        return bytes;
    }

    private sealed class FakeJxrDecoder(byte[] result) : ICziTileDecoder
    {
        public bool CanDecode(int compression) => compression == 4;

        public bool TryDecode(int compression, byte[] data, int width, int height, CameraPixelFormat format, out byte[] pixels, out string error)
        {
            pixels = result;
            error = string.Empty;
            return true;
        }
    }

    /// <summary>Builds a minimal ZISRAW file laid out like the published format description.</summary>
    private static class CziFile
    {
        public sealed record Tile(
            int PixelType,
            int Compression,
            int X,
            int Y,
            int Width,
            int Height,
            byte[] Data,
            int Channel = 0,
            byte Pyramid = 0,
            int Time = 0);

        private const int HeaderSegmentData = 512;

        public static byte[] Build(IReadOnlyList<Tile> tiles, bool includeDirectory = true, string? metadataXml = null)
        {
            using var file = new MemoryStream();
            WriteSegment(file, "ZISRAWFILE", HeaderSegmentData, new byte[80]);

            long metadataPosition = 0;
            if (metadataXml is not null)
            {
                metadataPosition = file.Position;
                var xml = Encoding.UTF8.GetBytes(metadataXml);
                var data = new byte[256 + xml.Length];
                BinaryPrimitives.WriteInt32LittleEndian(data, xml.Length);
                xml.CopyTo(data, 256);
                WriteSegment(file, "ZISRAWMETADATA", data.Length, data);
            }

            var positions = new List<long>();
            foreach (var tile in tiles)
            {
                positions.Add(file.Position);
                var entry = Entry(tile, file.Position);
                var fixedPart = Math.Max(256, 16 + entry.Length);
                var data = new byte[fixedPart + tile.Data.Length];
                BinaryPrimitives.WriteInt32LittleEndian(data, 0);                        // metadata size
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 0);              // attachment size
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), tile.Data.Length);
                entry.CopyTo(data, 16);
                tile.Data.CopyTo(data, fixedPart);
                WriteSegment(file, "ZISRAWSUBBLOCK", data.Length, data);
            }

            long directoryPosition = 0;
            if (includeDirectory)
            {
                directoryPosition = file.Position;
                var directory = new List<byte>();
                directory.AddRange(BitConverter.GetBytes(tiles.Count));
                directory.AddRange(new byte[124]);
                for (var i = 0; i < tiles.Count; i++)
                    directory.AddRange(Entry(tiles[i], positions[i]));
                WriteSegment(file, "ZISRAWDIRECTORY", directory.Count, directory.ToArray());
            }

            var bytes = file.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32 + 52), directoryPosition);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32 + 60), metadataPosition);
            return bytes;
        }

        private static byte[] Entry(Tile tile, long filePosition)
        {
            const int dimensions = 4; // X, Y, C, T
            var entry = new byte[32 + (dimensions * 20)];
            entry[0] = (byte)'D';
            entry[1] = (byte)'V';
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(2), tile.PixelType);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(6), filePosition);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(18), tile.Compression);
            entry[22] = tile.Pyramid;
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(28), dimensions);
            WriteDimension(entry, 0, 'X', tile.X, tile.Width);
            WriteDimension(entry, 1, 'Y', tile.Y, tile.Height);
            WriteDimension(entry, 2, 'C', tile.Channel, 1);
            WriteDimension(entry, 3, 'T', tile.Time, 1);
            return entry;
        }

        private static void WriteDimension(byte[] entry, int index, char id, int start, int size)
        {
            var offset = 32 + (index * 20);
            entry[offset] = (byte)id;
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(offset + 4), start);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(offset + 8), size);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(offset + 16), size);
        }

        private static void WriteSegment(Stream file, string id, int allocated, byte[] data)
        {
            var header = new byte[32];
            Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), allocated);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), data.Length);
            file.Write(header);
            file.Write(data);
            if (allocated > data.Length)
                file.Write(new byte[allocated - data.Length]);
        }
    }
}
