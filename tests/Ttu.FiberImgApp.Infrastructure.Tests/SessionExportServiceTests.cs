using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Configuration;
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
