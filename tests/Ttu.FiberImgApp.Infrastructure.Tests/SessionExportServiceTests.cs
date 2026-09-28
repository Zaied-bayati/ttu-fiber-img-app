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
    public async Task SaveSessionAsync_UsesUniqueFolderPerSessionId()
    {
        var root = Path.Combine(Path.GetTempPath(), "fiberimg-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var settings = new AppSettingsState { SessionsRootPath = root };
            var options = Options.Create(new CaptureOptions());
            var sut = new SessionExportService(options, settings, NullLogger<SessionExportService>.Instance);

            var started = DateTimeOffset.Parse("2026-09-15T14:30:00-05:00");
            var sessionA = MakeSession(started, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
            var sessionB = MakeSession(started, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

            var pathA = await sut.SaveSessionAsync(sessionA);
            var pathB = await sut.SaveSessionAsync(sessionB);

            Assert.NotEqual(pathA, pathB);
            Assert.True(Directory.Exists(pathA));
            Assert.True(Directory.Exists(pathB));
            Assert.True(File.Exists(Path.Combine(pathA, "session.json")));
            Assert.True(File.Exists(Path.Combine(pathB, "session.json")));
            Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", pathA, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", pathB, StringComparison.OrdinalIgnoreCase);
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
