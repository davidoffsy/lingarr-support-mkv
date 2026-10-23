using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services.Plex;

public class PlexTranslationTargetFactory : IPlexTranslationTargetFactory
{
    private readonly LingarrDbContext _dbContext;

    public PlexTranslationTargetFactory(LingarrDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PlexTranslationTarget?> TryCreateAsync(
        PlexMediaResolveResult resolved,
        CancellationToken cancellationToken = default)
    {
        if (!resolved.Found || resolved.MediaType is not MediaType mediaType)
        {
            return null;
        }

        var displayTitle = string.IsNullOrWhiteSpace(resolved.Title)
            ? FallbackTitle(mediaType)
            : resolved.Title;

        if (!resolved.IsPathOnly && resolved.EntityId > 0)
        {
            return await TryCreateFromLibraryAsync(resolved, mediaType, displayTitle, cancellationToken);
        }

        if (!PlexMappedPath.TrySplit(resolved.MappedPath, out var directory, out var stem, out var extension))
        {
            return null;
        }

        return new PlexTranslationTarget
        {
            MediaType = mediaType,
            Directory = directory,
            FileName = stem,
            FileExtension = extension,
            DisplayTitle = displayTitle,
            RatingKey = resolved.RatingKey,
            MappedFilePath = resolved.MappedPath
        };
    }

    private async Task<PlexTranslationTarget?> TryCreateFromLibraryAsync(
        PlexMediaResolveResult resolved,
        MediaType mediaType,
        string displayTitle,
        CancellationToken cancellationToken)
    {
        var media = await LoadMediaAsync(mediaType, resolved.EntityId, cancellationToken);
        if (media == null || string.IsNullOrEmpty(media.Path) || string.IsNullOrEmpty(media.FileName))
        {
            return null;
        }

        return new PlexTranslationTarget
        {
            MediaType = mediaType,
            MediaId = media.Id,
            Directory = media.Path.Replace('\\', '/').TrimEnd('/'),
            FileName = media.FileName,
            FileExtension = media.FileExtension,
            DisplayTitle = displayTitle,
            RatingKey = resolved.RatingKey,
            MappedFilePath = resolved.MappedPath
        };
    }

    private async Task<IMedia?> LoadMediaAsync(
        MediaType mediaType,
        int entityId,
        CancellationToken cancellationToken) =>
        mediaType switch
        {
            MediaType.Movie => await _dbContext.Movies
                .FirstOrDefaultAsync(movie => movie.Id == entityId, cancellationToken),
            MediaType.Episode => await _dbContext.Episodes
                .FirstOrDefaultAsync(episode => episode.Id == entityId, cancellationToken),
            _ => null
        };

    private static string FallbackTitle(MediaType mediaType) =>
        mediaType == MediaType.Episode ? "Unknown Episode" : "Unknown Movie";
}
