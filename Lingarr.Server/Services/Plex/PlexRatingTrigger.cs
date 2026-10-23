using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Services.Media;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services.Plex;

public class PlexRatingTrigger : IPlexRatingTrigger
{
    private static readonly TranslationStatus[] BlockingStatuses =
    [
        TranslationStatus.Pending,
        TranslationStatus.InProgress,
        TranslationStatus.Completed
    ];

    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly IMediaFileLocator _mediaFileLocator;
    private readonly IEmbeddedSubtitleService _embeddedSubtitleService;
    private readonly ISettingService _settingService;
    private readonly ITranslationRequestService _translationRequestService;
    private readonly IPlexSubtitleImporter _plexSubtitleImporter;
    private readonly ILogger<PlexRatingTrigger> _logger;

    public PlexRatingTrigger(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        IMediaFileLocator mediaFileLocator,
        IEmbeddedSubtitleService embeddedSubtitleService,
        ISettingService settingService,
        ITranslationRequestService translationRequestService,
        IPlexSubtitleImporter plexSubtitleImporter,
        ILogger<PlexRatingTrigger> logger)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _mediaFileLocator = mediaFileLocator;
        _embeddedSubtitleService = embeddedSubtitleService;
        _settingService = settingService;
        _translationRequestService = translationRequestService;
        _plexSubtitleImporter = plexSubtitleImporter;
        _logger = logger;
    }

    public async Task EnqueueAsync(
        PlexTranslationTarget target,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(target.Directory) || string.IsNullOrEmpty(target.FileName))
        {
            _logger.LogInformation(
                "Plex rating trigger skipped: Path or FileName is null for {MediaType} {Title}",
                target.MediaType,
                target.DisplayTitle);
            return;
        }

        var sidecars = await _subtitleService.GetSubtitles(target.Directory, target.FileName);
        var sourceLanguages = SourceLanguagePicker.CodesInOrder(
            await _settingService.GetSettingAsJson<SourceLanguage>(SettingKeys.Translation.SourceLanguages));
        var targetLanguages = SourceLanguagePicker.CodesInOrder(
            await _settingService.GetSettingAsJson<TargetLanguage>(SettingKeys.Translation.TargetLanguages));
        var sidecarPresent = TargetSubtitlePresence.FullLanguages(sidecars);
        if (TargetSubtitlePresence.CoversAll(sidecarPresent, targetLanguages))
        {
            _logger.LogInformation(
                "Plex rating trigger skipped: target language(s) already present for {FileName}: {Languages}",
                target.FileName,
                string.Join(", ", targetLanguages));
            return;
        }

        var mkvPath = FindMkv(target);
        var probe = await ProbeAsync(mkvPath, target.FileName, cancellationToken);
        var tracks = probe?.Tracks ?? [];
        var candidates = PlexSubtitleCandidateMerger.Merge(sidecars, tracks, mkvPath);
        var ignoreCaptions = await _settingService.GetSetting(SettingKeys.Translation.IgnoreCaptions) == "true";

        var (selected, plexCount) = await ResolveSourceAsync(
            candidates,
            sourceLanguages,
            ignoreCaptions,
            target.RatingKey,
            target.MediaType,
            target.Directory,
            target.FileName,
            cancellationToken);
        if (selected == null)
        {
            _logger.LogInformation(
                "Plex rating trigger skipped: no source subtitle for {MediaType} {Title} ({FileName}); sidecar languages [{SidecarLanguages}], plex text streams {PlexCount}",
                target.MediaType,
                target.DisplayTitle,
                target.FileName,
                string.Join(", ", sidecars.Select(sidecar => sidecar.Language).Distinct()),
                plexCount);
            return;
        }

        if (targetLanguages.Count == 0)
        {
            _logger.LogInformation(
                "Plex rating trigger skipped: no target languages configured for {MediaType} {Title}",
                target.MediaType,
                target.DisplayTitle);
            return;
        }

        var presentLanguages = TargetSubtitlePresence.FullLanguages(sidecars, probe);
        var existingTargetSet = await LoadBlockingTargetLanguagesAsync(target, cancellationToken);

        var subtitlePath = selected.IsEmbedded
            ? EmbeddedSubtitlePlaceholder.Create(selected.Path, selected.TrackIndex)
            : selected.Path;

        var enqueued = 0;
        foreach (var targetLanguage in targetLanguages)
        {
            if (TargetSubtitlePresence.Covers(presentLanguages, targetLanguage))
            {
                _logger.LogInformation(
                    "Plex rating trigger skipped target {TargetLanguage}: already present for {FileName}",
                    targetLanguage,
                    target.FileName);
                continue;
            }

            if (existingTargetSet.Contains(targetLanguage))
            {
                _logger.LogInformation(
                    "Plex rating trigger skipped target {TargetLanguage}: request already exists for {FileName}",
                    targetLanguage,
                    target.FileName);
                continue;
            }

            await _translationRequestService.CreateRequest(new TranslateAbleSubtitle
            {
                MediaId = target.MediaId,
                Title = target.DisplayTitle,
                MediaType = target.MediaType,
                SubtitlePath = subtitlePath,
                SourceLanguage = selected.Language,
                TargetLanguage = targetLanguage,
                SubtitleFormat = selected.Format
            });
            enqueued++;

            _logger.LogInformation(
                "Plex rating trigger queued {SourceLanguage} -> {TargetLanguage} for {SubtitlePath}",
                selected.Language,
                targetLanguage,
                subtitlePath);
        }

        if (enqueued == 0)
        {
            _logger.LogInformation(
                "Plex rating trigger skipped: all target languages already present or requested for {FileName}",
                target.FileName);
        }
    }

    /// <summary>
    /// Configured local language, then tagged Plex OpenSubtitles, then untagged
    /// sidecar, then a unique undetermined embedded track.
    /// </summary>
    private async Task<(PlexSourceSubtitleCandidate? Selected, int PlexCount)> ResolveSourceAsync(
        IReadOnlyList<PlexSourceSubtitleCandidate> localCandidates,
        IReadOnlyList<string> sourceLanguages,
        bool ignoreCaptions,
        string? ratingKey,
        MediaType mediaType,
        string mediaPath,
        string mediaFileName,
        CancellationToken cancellationToken)
    {
        if (sourceLanguages.Count == 0)
        {
            return (null, 0);
        }

        var selected = PlexSourceSubtitleSelector.SelectConfigured(
            localCandidates,
            sourceLanguages,
            ignoreCaptions);
        if (selected != null)
        {
            return (selected, 0);
        }

        IReadOnlyList<PlexSourceSubtitleCandidate> plexCandidates = [];
        if (!string.IsNullOrWhiteSpace(ratingKey))
        {
            plexCandidates = await _plexSubtitleImporter.ListAsync(
                ratingKey,
                mediaType,
                cancellationToken);

            selected = PlexSourceSubtitleSelector.SelectConfigured(
                plexCandidates,
                sourceLanguages,
                ignoreCaptions);
            if (selected?.IsPlexStream == true)
            {
                var importedPath = await _plexSubtitleImporter.MaterializeAsync(
                    selected,
                    mediaPath,
                    mediaFileName,
                    cancellationToken);
                if (string.IsNullOrEmpty(importedPath))
                {
                    _logger.LogInformation(
                        "Plex subtitle import failed for {StreamKey} ({FileName}); falling back to untagged local sources.",
                        selected.StreamKey,
                        mediaFileName);
                    selected = null;
                }
                else
                {
                    selected = CloneWithPath(selected, importedPath);
                }
            }
        }

        selected ??= PlexSourceSubtitleSelector.SelectUntaggedSidecar(localCandidates, ignoreCaptions)
            ?? PlexSourceSubtitleSelector.SelectUniqueUndeterminedEmbedded(localCandidates, ignoreCaptions);

        return (selected, plexCandidates.Count);
    }

    private async Task<HashSet<string>> LoadBlockingTargetLanguagesAsync(
        PlexTranslationTarget target,
        CancellationToken cancellationToken)
    {
        var mediaId = target.MediaId;
        var mediaType = target.MediaType;
        var prefix = target.Directory.TrimEnd('/') + "/";
        var embeddedPrefix = EmbeddedSubtitlePlaceholder.Prefix + prefix;
        var embeddedDirectory = EmbeddedSubtitlePlaceholder.Prefix + target.Directory;

        var candidates = await _dbContext.TranslationRequests
            .Where(request =>
                BlockingStatuses.Contains(request.Status)
                && (
                    (mediaId != null && request.MediaId == mediaId && request.MediaType == mediaType)
                    || (request.SubtitleToTranslate != null && (
                        request.SubtitleToTranslate.StartsWith(prefix)
                        || request.SubtitleToTranslate.StartsWith(embeddedPrefix)
                        || request.SubtitleToTranslate.StartsWith(embeddedDirectory)))))
            .Select(request => new
            {
                request.MediaId,
                request.MediaType,
                request.TargetLanguage,
                request.SubtitleToTranslate
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(request =>
                (mediaId != null && request.MediaId == mediaId && request.MediaType == mediaType)
                || PlexVideoRequestMatcher.IsSameVideo(
                    request.SubtitleToTranslate,
                    target.Directory,
                    target.FileName))
            .Select(request => request.TargetLanguage)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? FindMkv(PlexTranslationTarget target)
    {
        if (!MediaFileExtension.AllowsMkvLookup(target.FileExtension))
        {
            return null;
        }

        return _mediaFileLocator.FindMkv(target.Directory, target.FileName);
    }

    private async Task<EmbeddedSubtitleProbeResult?> ProbeAsync(
        string? mkvPath,
        string? fileName,
        CancellationToken cancellationToken)
    {
        if (mkvPath == null)
        {
            return null;
        }

        try
        {
            return await _embeddedSubtitleService.ProbeDetailedAsync(mkvPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to probe embedded subtitles for Plex media {FileName}; continuing with sidecars only.",
                fileName);
            return null;
        }
    }

    private static PlexSourceSubtitleCandidate CloneWithPath(
        PlexSourceSubtitleCandidate selected,
        string path) =>
        new()
        {
            Path = path,
            Language = selected.Language,
            Caption = selected.Caption,
            Format = selected.Format,
            IsEmbedded = false,
            TrackIndex = selected.TrackIndex,
            SidecarOrder = selected.SidecarOrder,
            IsForced = selected.IsForced,
            IsHearingImpaired = selected.IsHearingImpaired,
            IsPlexStream = false,
            StreamKey = selected.StreamKey
        };
}
