using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Infrastructure.Sampling;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public sealed class SessionExportServiceTests
{
    [Fact]
    public async Task SaveSessionAsync_AppendsNumericSuffixOnCollision()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var options = Options.Create(new CaptureOptions());
            var sut = new SessionExportService(options, settings, NullLogger<SessionExportService>.Instance);

            var started = DateTimeOffset.Parse("2026-09-15T14:30:00-05:00");
            var sessionA = MakeSession(started, Guid.NewGuid());
            var sessionB = MakeSession(started, Guid.NewGuid());

            var pathA = await sut.SaveSessionAsync(sessionA);
            var pathB = await sut.SaveSessionAsync(sessionB);

            Assert.NotEqual(pathA, pathB);
            Assert.True(Directory.Exists(pathA));
            Assert.True(Directory.Exists(pathB));
            Assert.True(File.Exists(Path.Combine(pathA, "session.json")));
            Assert.True(File.Exists(Path.Combine(pathB, "session.json")));
            Assert.Equal(Path.GetFileName(pathA) + "_2", Path.GetFileName(pathB));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSessionAsync_SanitizesInvalidCharactersInCustomName()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var sut = new SessionExportService(Options.Create(new CaptureOptions()), settings, NullLogger<SessionExportService>.Instance);
            var session = MakeSession(DateTimeOffset.Parse("2026-09-15T14:30:00-05:00"), Guid.NewGuid());

            var folder = await sut.SaveSessionAsync(session, customName: "Bad:Name?");
            var folderName = Path.GetFileName(folder);

            Assert.All(Path.GetInvalidFileNameChars(), ch => Assert.DoesNotContain(ch, folderName));
            Assert.Equal("Bad_Name", folderName);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSessionAsync_BlankCustomName_FallsBackToDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var sut = new SessionExportService(Options.Create(new CaptureOptions()), settings, NullLogger<SessionExportService>.Instance);
            var started = DateTimeOffset.Parse("2026-09-15T14:30:00-05:00");
            var session = MakeSession(started, Guid.NewGuid());

            var folder = await sut.SaveSessionAsync(session, customName: "   ");

            Assert.Equal("2026-09-15_14-30-00", Path.GetFileName(folder));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSessionAsync_CustomName_CollisionGetsNumericSuffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var sut = new SessionExportService(Options.Create(new CaptureOptions()), settings, NullLogger<SessionExportService>.Instance);
            var sessionA = MakeSession(DateTimeOffset.Parse("2026-09-15T14:30:00-05:00"), Guid.NewGuid());
            var sessionB = MakeSession(DateTimeOffset.Parse("2026-09-15T15:00:00-05:00"), Guid.NewGuid());

            var pathA = await sut.SaveSessionAsync(sessionA, customName: "Fiber Batch 1");
            var pathB = await sut.SaveSessionAsync(sessionB, customName: "Fiber Batch 1");

            Assert.Equal("Fiber Batch 1", Path.GetFileName(pathA));
            Assert.Equal("Fiber Batch 1_2", Path.GetFileName(pathB));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSessionAsync_CopiesCziWithoutOverwriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var czi = Path.Combine(root, "source.czi");
        await File.WriteAllTextAsync(czi, "czi-master");

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var sut = new SessionExportService(Options.Create(new CaptureOptions()), settings, NullLogger<SessionExportService>.Instance);
            var session = MakeSession(DateTimeOffset.Parse("2026-09-28T14:08:12-05:00"), Guid.NewGuid());
            session.Images[0].CziPath = czi;

            var folder = await sut.SaveSessionAsync(session);
            var copied = Directory.GetFiles(folder, "*.czi");
            Assert.Single(copied);
            Assert.Equal("czi-master", await File.ReadAllTextAsync(copied[0]));
            Assert.True(File.Exists(czi));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSessionAsync_CopiesBmpAlongsidePng()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bmp = Path.Combine(root, "source.bmp");
        await File.WriteAllBytesAsync(bmp, new byte[] { (byte)'B', (byte)'M', 0, 0 });

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var sut = new SessionExportService(Options.Create(new CaptureOptions()), settings, NullLogger<SessionExportService>.Instance);
            var session = MakeSession(DateTimeOffset.Parse("2026-09-28T14:08:12-05:00"), Guid.NewGuid());
            session.Images[0].BmpPath = bmp;

            var folder = await sut.SaveSessionAsync(session);
            var copied = Directory.GetFiles(folder, "*.bmp");
            Assert.Single(copied);
            Assert.True(File.Exists(Directory.GetFiles(folder, "*.png").Single()));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BmpWriter_TruncatesGray16ToHighByte()
    {
        var path = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N") + ".bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // Two Gray16 samples: 0x03E8 (1000, high byte 0x03) and 0xFFFF (65535, high byte 0xFF).
            var pixels = new byte[] { 0xE8, 0x03, 0xFF, 0xFF };
            MonoBmpWriter.Write(path, new CameraFrame
            {
                RawPixels = pixels,
                Width = 2,
                Height = 1,
                PixelFormat = CameraPixelFormat.Gray16,
            });

            var written = File.ReadAllBytes(path);
            Assert.Equal((byte)'B', written[0]);
            Assert.Equal((byte)'M', written[1]);

            var pixelDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(10));
            Assert.Equal(3, written[(int)pixelDataOffset]);
            Assert.Equal(255, written[(int)pixelDataOffset + 1]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void TiffWriter_PreservesGray16Samples()
    {
        var path = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N") + ".tif");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var pixels = new byte[] { 0x00, 0x00, 0xE8, 0x03 };
            MonoTiffWriter.Write(path, new CameraFrame
            {
                RawPixels = pixels,
                Width = 2,
                Height = 1,
                PixelFormat = CameraPixelFormat.Gray16,
            });

            var written = File.ReadAllBytes(path);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(8)));
            Assert.Equal(1000, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(10)));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static SamplingSession MakeSession(DateTimeOffset startedAt, Guid id)
    {
        var session = new SamplingSession
        {
            Id = id,
            StartedAt = startedAt,
            MinMm = 0,
            MaxMm = 10,
            RequestedCount = 1
        };
        session.Images.Add(new CapturedImage
        {
            Index = 1,
            PositionMm = 0,
            CapturedAt = startedAt,
            PngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            Keep = true
        });
        return session;
    }
}
