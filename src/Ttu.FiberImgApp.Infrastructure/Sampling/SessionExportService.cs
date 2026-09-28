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

        // Include session Id so two saves in the same clock second cannot overwrite each other.
        var folderName = $"{session.StartedAt.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{session.Id:N}";
        var sessionDir = Path.Combine(root, folderName);
        if (Directory.Exists(sessionDir))
        {
            throw new IOException($"Session folder already exists: {sessionDir}");
        }

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
            var stamp = image.CapturedAt.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
            var baseName = $"{stamp}_{image.Index:000}";
            var fileName = UniqueName(sessionDir, baseName, ".png");
            var path = Path.Combine(sessionDir, fileName);
            await File.WriteAllBytesAsync(path, image.PngBytes, cancellationToken).ConfigureAwait(false);

            var cziName = CopyIfPresent(sessionDir, baseName, ".czi", image.CziPath);
            var tiffName = CopyIfPresent(sessionDir, baseName, ".tif", image.TiffPath);

            metadata.Add(new
            {
                image.Index,
                image.PositionMm,
                CapturedAt = image.CapturedAt,
                image.Width,
                image.Height,
                image.PixelFormat,
                FileName = fileName,
                CziFileName = cziName,
                TiffFileName = tiffName,
                SourceCzi = image.CziPath
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

    private static string? CopyIfPresent(string sessionDir, string baseName, string extension, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return null;

        var fileName = UniqueName(sessionDir, baseName, extension);
        var destination = Path.Combine(sessionDir, fileName);
        File.Copy(sourcePath, destination, overwrite: false);
        return fileName;
    }

    private static string UniqueName(string directory, string baseName, string extension)
    {
        var candidate = baseName + extension;
        var path = Path.Combine(directory, candidate);
        var suffix = 2;
        while (File.Exists(path))
        {
            candidate = $"{baseName}_{suffix}{extension}";
            path = Path.Combine(directory, candidate);
            suffix++;
        }

        return candidate;
    }
}
