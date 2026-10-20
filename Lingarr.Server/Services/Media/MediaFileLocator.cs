using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services;

namespace Lingarr.Server.Services.Media;

public class MediaFileLocator : IMediaFileLocator
{
    public const string MkvExtension = ".mkv";

    private readonly ILogger<MediaFileLocator> _logger;

    public MediaFileLocator(ILogger<MediaFileLocator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string? FindMkv(IMedia media)
    {
        if (media == null)
        {
            return null;
        }

        if (!MediaFileExtension.AllowsMkvLookup(media.FileExtension))
        {
            return null;
        }

        return FindMkv(media.Path, media.FileName);
    }

    /// <inheritdoc />
    public string? FindMkv(string? directory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                _logger.LogDebug("Media directory {Directory} does not exist; skipping MKV lookup.", directory);
                return null;
            }

            var directPath = Path.Combine(directory, fileName + MkvExtension);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            var matches = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!IsMkv(file) || !MatchesStem(file, fileName))
                {
                    continue;
                }

                matches.Add(file);
            }

            var selected = matches
                .OrderBy(path => !string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => Path.GetFileName(path).Length)
                .FirstOrDefault();

            if (selected == null)
            {
                _logger.LogDebug(
                    "No MKV found for stem {FileName} in {Directory}.",
                    fileName,
                    directory);
            }

            return selected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to locate MKV for {FileName} in {Directory}.", fileName, directory);
            return null;
        }
    }

    private static bool IsMkv(string path) =>
        string.Equals(Path.GetExtension(path), MkvExtension, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesStem(string path, string fileName)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(fileName + ".", StringComparison.Ordinal) ||
               string.Equals(Path.GetFileNameWithoutExtension(path), fileName, StringComparison.Ordinal);
    }
}
