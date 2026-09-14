using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Infrastructure.Sampling;

public sealed class SessionExportService : ISessionExportService
{
    private readonly CaptureOptions _options;
    private readonly AppSettingsState _settings;
    private readonly ILogger<SessionExportService> _logger;

    public SessionExportService(
        IOptions<CaptureOptions> options,
        AppSettingsState settings,
        ILogger<SessionExportService> logger)
    {
        _options = options.Value;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> SaveSessionAsync(SamplingSession session, CancellationToken cancellationToken = default)
    {
        var root = !string.IsNullOrWhiteSpace(_settings.SessionsRootPath)
            ? _settings.SessionsRootPath
            : !string.IsNullOrWhiteSpace(_options.SessionsRootPath)
                ? _options.SessionsRootPath
                : AppSettingsState.GetDefaultSessionsRoot();

        Directory.CreateDirectory(root);

        var folderName = session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss");
        var sessionDir = Path.Combine(root, folderName);
        Directory.CreateDirectory(sessionDir);

        var kept = session.Images.Where(i => i.Keep).OrderBy(i => i.Index).ToList();
        if (kept.Count == 0)
        {
            throw new InvalidOperationException("No images left to save. Keep at least one image.");
        }

        var metadata = new List<object>();
        foreach (var image in kept)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stamp = image.CapturedAt.ToLocalTime().ToString("yyyyMMdd_HHmmss");
            var fileName = $"{stamp}_{image.Index:000}.png";
            var path = Path.Combine(sessionDir, fileName);
            await File.WriteAllBytesAsync(path, image.PngBytes, cancellationToken).ConfigureAwait(false);

            metadata.Add(new
            {
                image.Index,
                image.PositionMm,
                CapturedAt = image.CapturedAt,
                FileName = fileName
            });
        }

        var metaPath = Path.Combine(sessionDir, "session.json");
        var meta = new
        {
            session.Id,
            session.StartedAt,
            session.MinMm,
            session.MaxMm,
            session.RequestedCount,
            SavedCount = kept.Count,
            Images = metadata
        };

        await File.WriteAllTextAsync(
            metaPath,
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Saved {Count} images to {Folder}", kept.Count, sessionDir);
        return sessionDir;
    }
}
