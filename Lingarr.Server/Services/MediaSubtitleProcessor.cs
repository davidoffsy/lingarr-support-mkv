using System.Security.Cryptography;
using Lingarr.Contracts.Interfaces;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Media;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

public class MediaSubtitleProcessor : IMediaSubtitleProcessor
{
    private readonly ITranslationRequestService _translationRequestService;
    private readonly ILogger<IMediaSubtitleProcessor> _logger;
    private readonly ISubtitleService _subtitleService;
    private readonly ISettingService _settingService;
    private readonly IMediaFileLocator _mediaFileLocator;
    private readonly IEmbeddedSubtitleService _embeddedSubtitleService;
    private readonly LingarrDbContext _dbContext;
    private string _hash = string.Empty;
    private IMedia _media = null!;
    private MediaType _mediaType;

    public MediaSubtitleProcessor(
        ITranslationRequestService translationRequestService,
        ILogger<IMediaSubtitleProcessor> logger,
        ISettingService settingService,
        ISubtitleService subtitleService,
        IMediaFileLocator mediaFileLocator,
        IEmbeddedSubtitleService embeddedSubtitleService,
        LingarrDbContext dbContext)
    {
        _translationRequestService = translationRequestService;
        _settingService = settingService;
        _subtitleService = subtitleService;
        _mediaFileLocator = mediaFileLocator;
        _embeddedSubtitleService = embeddedSubtitleService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ProcessMedia(
        IMedia media, 
        MediaType mediaType)
    {
        if (media.Path == null || media.FileName == null)
        {
            _logger.LogWarning(
                "Skipping media processing: Path or FileName is null for {MediaType} with ID {MediaId}",
                mediaType, media.Id);
            return false;
        }
        
        var subtitles = await _subtitleService.GetSubtitles(media.Path, media.FileName);
        var (mkvPath, probe) = await CollectEmbeddedSubtitles(media, subtitles);

        if (!subtitles.Any())
        {
            return false;
        }

        var sourceLanguages = await GetLanguagesSetting<SourceLanguage>(SettingKeys.Translation.SourceLanguages);
        var targetLanguages = await GetLanguagesSetting<TargetLanguage>(SettingKeys.Translation.TargetLanguages);
        var ignoreCaptions = await _settingService.GetSetting(SettingKeys.Translation.IgnoreCaptions) ?? "false";

        _media = media;
        _mediaType = mediaType;
        var presentLanguages = TargetSubtitlePresence.FullLanguages(subtitles, probe);
        _hash = CreateHash(subtitles, sourceLanguages, targetLanguages, ignoreCaptions, presentLanguages);
        if (!string.IsNullOrEmpty(media.MediaHash) && media.MediaHash == _hash)
        {
            return false;
        }
        
        _logger.LogInformation("Initiating subtitle processing.");
        return await ProcessSubtitles(
            subtitles,
            sourceLanguages,
            targetLanguages,
            ignoreCaptions,
            mkvPath,
            presentLanguages);
    }

    /// <summary>
    /// Always probes the MKV so image and text tracks can mark a target language as
    /// already present. Extractable text tracks are merged as translation sources
    /// only when scanning is enabled.
    /// </summary>
    private async Task<(string? MkvPath, EmbeddedSubtitleProbeResult? Probe)> CollectEmbeddedSubtitles(
        IMedia media,
        List<Subtitles> subtitles)
    {
        var mkvPath = _mediaFileLocator.FindMkv(media);
        if (mkvPath == null)
        {
            return (null, null);
        }

        try
        {
            var probe = await _embeddedSubtitleService.ProbeDetailedAsync(mkvPath);
            var scanEmbedded = await _settingService.GetSetting(SettingKeys.Translation.ScanEmbeddedSubtitles) == "true";
            if (scanEmbedded && probe is { Found: true })
            {
                var merged = EmbeddedSubtitleMerger.Merge(subtitles, probe.Tracks, mkvPath);
                subtitles.Clear();
                subtitles.AddRange(merged);
            }

            return (mkvPath, probe);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to probe embedded subtitles for {FileName}; continuing with sidecars only.",
                media.FileName);
            return (mkvPath, null);
        }
    }

    /// <summary>
    /// Processes subtitle files for translation based on configured languages.
    /// </summary>
    /// <param name="subtitles">List of subtitle files to process.</param>
    /// <param name="sourceLanguages">The source languages.</param>
    /// <param name="targetLanguages">The target languages.</param>
    /// <param name="ignoreCaptions">The ignore captions setting.</param>
    /// <param name="mkvPath">MKV path when embedded tracks were probed; otherwise null.</param>
    /// <param name="presentLanguages">Full (non-forced) languages already present on disk or in the MKV.</param>
    /// <returns>True if new translation requests were created, false otherwise.</returns>
    private async Task<bool> ProcessSubtitles(
        List<Subtitles> subtitles,
        IReadOnlyList<string> sourceLanguages,
        IReadOnlyList<string> targetLanguages,
        string ignoreCaptions,
        string? mkvPath,
        ISet<string> presentLanguages)
    {
        if (sourceLanguages.Count == 0 || targetLanguages.Count == 0)
        {
            _logger.LogWarning(
                "Source or target languages are empty. Source languages: {SourceCount}, Target languages: {TargetCount}",
                sourceLanguages.Count, targetLanguages.Count);
            await UpdateHash();
            return false;
        }

        var selected = _subtitleService.SelectSourceSubtitle(subtitles, sourceLanguages, ignoreCaptions);
        if (selected == null || !targetLanguages.Any())
        {
            _logger.LogWarning(
                "No valid source language or target languages found for media |Green|{FileName}|/Green|. " +
                "Existing languages: |Red|{ExistingLanguages}|/Red|, " +
                "Source languages: |Red|{SourceLanguages}|/Red|, " +
                "Target languages: |Red|{TargetLanguages}|/Red|",
                string.Join(", ", _media?.FileName),
                string.Join(", ", subtitles.Select(s => s.Language.ToLowerInvariant())),
                string.Join(", ", sourceLanguages),
                string.Join(", ", targetLanguages));

            await UpdateHash();
            return false;
        }

        var languagesToTranslate = targetLanguages
            .Where(language => !presentLanguages.Contains(language))
            .ToList();
        if (languagesToTranslate.Count != targetLanguages.Count)
        {
            _logger.LogInformation(
                "Skipping already-present target language(s) for |Green|{FileName}|/Green|: |Orange|{Languages}|/Orange|",
                _media.FileName,
                string.Join(", ", targetLanguages.Where(presentLanguages.Contains)));
        }
        
        var activeStatuses = new[]
        {
            TranslationStatus.Pending,
            TranslationStatus.InProgress,
            TranslationStatus.Completed
        };
        var existingTranslationRequests = await _dbContext.TranslationRequests
            .Where(translationRequest => translationRequest.MediaId == _media.Id
                                         && translationRequest.MediaType == _mediaType
                                         && activeStatuses.Contains(translationRequest.Status))
            .Select(translationRequest => translationRequest.TargetLanguage)
            .Distinct()
            .ToListAsync();

        languagesToTranslate = languagesToTranslate.Except(existingTranslationRequests).ToList();
        if (!languagesToTranslate.Any())
        {
            await UpdateHash();
            return false;
        }

        if (!await EnsureSidecarSource(selected.Subtitle, mkvPath))
        {
            return false;
        }

        foreach (var targetLanguage in languagesToTranslate)
        {
            await _translationRequestService.CreateRequest(new TranslateAbleSubtitle
            {
                MediaId = _media.Id,
                MediaType = _mediaType,
                SubtitlePath = selected.Subtitle.Path,
                TargetLanguage = targetLanguage,
                SourceLanguage = selected.SourceLanguage,
                SubtitleFormat = selected.Subtitle.Format
            });
            _logger.LogInformation(
                "Initiating translation from |Orange|{sourceLanguage}|/Orange| to |Orange|{targetLanguage}|/Orange| for |Green|{subtitleFile}|/Green|",
                selected.SourceLanguage,
                targetLanguage,
                selected.Subtitle.Path);
        }

        await UpdateHash();
        return true;
    }

    /// <summary>
    /// Copies an embedded source track to a real sidecar before enqueueing translation.
    /// TranslationJob only reads sidecar paths. Extract failure does not persist the hash
    /// so the next run can retry.
    /// </summary>
    private async Task<bool> EnsureSidecarSource(Subtitles subtitle, string? mkvPath)
    {
        if (!subtitle.IsEmbedded)
        {
            return true;
        }

        if (string.IsNullOrEmpty(mkvPath))
        {
            _logger.LogWarning(
                "Cannot extract embedded subtitle for {FileName}: MKV path is missing.",
                _media.FileName);
            return false;
        }

        var extractedPath = await _embeddedSubtitleService.ExtractAsync(
            mkvPath,
            EmbeddedSubtitleMerger.ToTrack(subtitle));
        if (string.IsNullOrEmpty(extractedPath))
        {
            _logger.LogWarning(
                "Failed to extract embedded subtitle stream {Index} from {MkvPath}; skipping translation.",
                subtitle.TrackIndex,
                mkvPath);
            return false;
        }

        subtitle.Path = extractedPath;
        subtitle.FileName = Path.GetFileNameWithoutExtension(extractedPath);
        subtitle.IsEmbedded = false;
        return true;
    }

    /// <summary>
    /// Creates a hash of the current subtitle file state.
    /// Embedded tracks are identified by index + codec + language so extracting a sidecar
    /// does not churn the hash into an infinite re-queue.
    /// </summary>
    /// <param name="subtitles">List of subtitle file paths to include in the hash.</param>
    /// <param name="sourceLanguages">The source languages.</param>
    /// <param name="targetLanguages">The target languages.</param>
    /// <param name="ignoreCaptions">The ignore captions setting.</param>
    /// <param name="presentLanguages">Full (non-forced) languages already present, included so PGS changes invalidate the hash.</param>
    /// <returns>A Base64 encoded string representing the hash of the current subtitle state.</returns>
    private string CreateHash(
        List<Subtitles> subtitles,
        IReadOnlyList<string> sourceLanguages,
        IReadOnlyList<string> targetLanguages,
        string ignoreCaptions,
        IReadOnlyCollection<string> presentLanguages)
    {
        using var sha256 = SHA256.Create();
        var subtitleKeys = string.Join("|", subtitles
            .Select(EmbeddedSubtitleMerger.Identity)
            .OrderBy(key => key));
        
        var sourceLangs = string.Join(",", sourceLanguages.OrderBy(l => l));
        var targetLangs = string.Join(",", targetLanguages.OrderBy(l => l));
        var presentLangs = string.Join(",", presentLanguages.OrderBy(l => l, StringComparer.Ordinal));
        
        var hashInput = $"{subtitleKeys}|{sourceLangs}|{targetLangs}|{ignoreCaptions}|{presentLangs}";
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Retrieves language settings from the application configuration.
    /// </summary>
    /// <typeparam name="T">The type of language setting to retrieve (Source or Target).</typeparam>
    /// <param name="settingName">The name of the setting to retrieve.</param>
    /// <returns>Language codes from the configuration, in settings list order.</returns>
    private async Task<List<string>> GetLanguagesSetting<T>(string settingName) where T : class, ILanguage
    {
        var languages = await _settingService.GetSettingAsJson<T>(settingName);
        return SourceLanguagePicker.CodesInOrder(languages);
    }

    /// <summary>
    /// Updates the media hash in the database.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task UpdateHash()
    {
        _media.MediaHash = _hash;
        _dbContext.Update(_media);
        await _dbContext.SaveChangesAsync();
    }
}
