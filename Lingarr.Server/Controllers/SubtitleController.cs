using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Attributes;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Controllers;

[ApiController]
[LingarrAuthorize]
[Route("api/[controller]")]
public class SubtitleController : ControllerBase
{
    private readonly ISubtitleService _subtitleService;
    private readonly LingarrDbContext _dbContext;
    private readonly IMediaFileLocator _mediaFileLocator;
    private readonly IEmbeddedSubtitleService _embeddedSubtitleService;
    private readonly ITranslationRequestService _translationRequestService;
    private readonly ILogger<SubtitleController> _logger;

    public SubtitleController(
        ISubtitleService subtitleService,
        LingarrDbContext dbContext,
        IMediaFileLocator mediaFileLocator,
        IEmbeddedSubtitleService embeddedSubtitleService,
        ITranslationRequestService translationRequestService,
        ILogger<SubtitleController> logger)
    {
        _subtitleService = subtitleService;
        _dbContext = dbContext;
        _mediaFileLocator = mediaFileLocator;
        _embeddedSubtitleService = embeddedSubtitleService;
        _translationRequestService = translationRequestService;
        _logger = logger;
    }
    
    /// <summary>
    /// Retrieves a list of subtitle files located at the specified path.
    /// </summary>
    /// <param name="subtitlePath">The directory path to search for subtitle files.This path is relative to the media folder
    /// and should not start with a forward slash.</param>
    /// <returns>Returns an HTTP 200 OK response with a list of <see cref="Subtitles"/> objects found at the specified path.</returns>
    [HttpPost("all")]
    public async Task<ActionResult<List<Subtitles>>> GetAllSubtitles([FromBody] SubtitlePath subtitlePath)
    {
        var value = await _subtitleService.GetAllSubtitles(subtitlePath.Path);
        return Ok(value);
    }

    /// <summary>
    /// Probes a single movie or episode MKV for extractable text subtitle tracks.
    /// Failures that the UI should display still return HTTP 200 with <c>found</c> / <c>reason</c>.
    /// </summary>
    [HttpPost("probe")]
    public async Task<ActionResult<ProbeEmbeddedResponse>> Probe(
        [FromBody] ProbeEmbeddedRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MediaType is not (MediaType.Movie or MediaType.Episode))
        {
            return BadRequest(new { Error = "mediaType must be Movie or Episode." });
        }

        var media = await FindMediaAsync(request.MediaId, request.MediaType, cancellationToken);
        if (media == null)
        {
            return NotFound();
        }

        var mkvPath = _mediaFileLocator.FindMkv(media);
        if (string.IsNullOrEmpty(mkvPath))
        {
            return Ok(new ProbeEmbeddedResponse
            {
                Found = false,
                Reason = EmbeddedSubtitleProbeResult.ReasonNoMkv
            });
        }

        EmbeddedSubtitleProbeResult probe;
        try
        {
            probe = await _embeddedSubtitleService.ProbeDetailedAsync(mkvPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe embedded subtitles for {MediaType} {MediaId}.", request.MediaType, request.MediaId);
            return Ok(new ProbeEmbeddedResponse
            {
                Found = false,
                Reason = EmbeddedSubtitleProbeResult.ReasonProbeFailed,
                FileName = Path.GetFileName(mkvPath)
            });
        }

        if (!probe.Found)
        {
            return Ok(new ProbeEmbeddedResponse
            {
                Found = false,
                Reason = probe.Reason,
                FileName = Path.GetFileName(mkvPath)
            });
        }

        return Ok(new ProbeEmbeddedResponse
        {
            Found = true,
            FileName = Path.GetFileName(mkvPath),
            Tracks = probe.Tracks.Select(track => MapTrack(mkvPath, track)).ToList(),
            Skipped = probe.Skipped.Select(skipped => new SkippedEmbeddedTrackDto
            {
                Index = skipped.Index,
                CodecName = skipped.CodecName,
                Reason = skipped.Reason,
                Language = skipped.Language,
                Caption = skipped.Caption,
                IsForced = skipped.IsForced
            }).ToList()
        });
    }

    /// <summary>
    /// Queues extraction of an embedded text track (if needed) and translation of the resulting sidecar.
    /// Returns immediately with a translation request id; ffmpeg runs inside Hangfire.
    /// </summary>
    [HttpPost("extract-and-translate")]
    public async Task<ActionResult<TranslationJobDto>> ExtractAndTranslate(
        [FromBody] ExtractAndTranslateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MediaType is not (MediaType.Movie or MediaType.Episode))
        {
            return BadRequest(new { Error = "mediaType must be Movie or Episode." });
        }

        if (request.TrackIndex < 0)
        {
            return BadRequest(new { Error = "trackIndex must be a non-negative stream index." });
        }

        if (string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            return BadRequest(new { Error = "targetLanguage is required." });
        }

        var media = await FindMediaAsync(request.MediaId, request.MediaType, cancellationToken);
        if (media == null)
        {
            return NotFound();
        }

        var mkvPath = _mediaFileLocator.FindMkv(media);
        if (string.IsNullOrEmpty(mkvPath))
        {
            return BadRequest(new { Error = "No MKV file was found for this media." });
        }

        EmbeddedSubtitleProbeResult probe;
        try
        {
            probe = await _embeddedSubtitleService.ProbeDetailedAsync(mkvPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to probe embedded subtitles before extract-and-translate for {MediaType} {MediaId}.",
                request.MediaType,
                request.MediaId);
            return BadRequest(new { Error = "Unable to read embedded subtitle tracks." });
        }

        if (!probe.Found)
        {
            return BadRequest(new { Error = ProbeFailureMessage(probe.Reason) });
        }

        var track = probe.Tracks.FirstOrDefault(candidate => candidate.Index == request.TrackIndex);
        if (track == null)
        {
            return BadRequest(new { Error = $"Track {request.TrackIndex} is not an extractable text subtitle." });
        }

        string sourceLanguage;
        if (RequiresUserSourceLanguage(track.Language))
        {
            if (string.IsNullOrWhiteSpace(request.SourceLanguage))
            {
                return BadRequest(new { Error = "sourceLanguage is required when the track language is und or unknown." });
            }

            sourceLanguage = request.SourceLanguage.Trim();
        }
        else
        {
            sourceLanguage = track.Language;
        }

        var sidecarPath = GetSidecarPath(mkvPath, track);
        var subtitlePath = HasNonEmptyFile(sidecarPath)
            ? sidecarPath
            : EmbeddedSubtitlePlaceholder.Create(mkvPath, track.Index);

        var jobId = await _translationRequestService.CreateRequest(new TranslateAbleSubtitle
        {
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            SubtitlePath = subtitlePath,
            SourceLanguage = sourceLanguage,
            TargetLanguage = request.TargetLanguage.Trim(),
            SubtitleFormat = track.Extension
        });

        return Ok(new TranslationJobDto
        {
            JobId = jobId
        });
    }

    private async Task<IMedia?> FindMediaAsync(int mediaId, MediaType mediaType, CancellationToken cancellationToken)
    {
        return mediaType switch
        {
            MediaType.Movie => await _dbContext.Movies
                .FirstOrDefaultAsync(movie => movie.Id == mediaId, cancellationToken),
            MediaType.Episode => await _dbContext.Episodes
                .FirstOrDefaultAsync(episode => episode.Id == mediaId, cancellationToken),
            _ => null
        };
    }

    private static ProbeEmbeddedTrackDto MapTrack(string mkvPath, EmbeddedSubtitleTrack track)
    {
        return new ProbeEmbeddedTrackDto
        {
            Index = track.Index,
            Language = track.Language,
            CodecName = track.CodecName,
            Extension = track.Extension,
            Title = track.Title,
            Caption = track.Caption,
            IsDefault = track.IsDefault,
            IsForced = track.IsForced,
            IsHearingImpaired = track.IsHearingImpaired,
            CanAutoTranslate = track.CanAutoTranslate,
            SidecarExists = HasNonEmptyFile(GetSidecarPath(mkvPath, track)),
            Extractable = true
        };
    }

    private static string GetSidecarPath(string mkvPath, EmbeddedSubtitleTrack track)
    {
        var directory = Path.GetDirectoryName(mkvPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(mkvPath);
        return Path.Combine(directory, track.BuildSidecarFileName(stem));
    }

    private static bool HasNonEmptyFile(string path)
    {
        try
        {
            return System.IO.File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool RequiresUserSourceLanguage(string language) =>
        string.Equals(language, EmbeddedSubtitleTrack.UnknownLanguage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, EmbeddedSubtitleTrack.UndeterminedLanguage, StringComparison.OrdinalIgnoreCase);

    private static string ProbeFailureMessage(string? reason) => reason switch
    {
        EmbeddedSubtitleProbeResult.ReasonNoMkv => "No MKV file was found for this media.",
        EmbeddedSubtitleProbeResult.ReasonNoFfprobe => "ffprobe was not found; embedded subtitle tracks cannot be read.",
        EmbeddedSubtitleProbeResult.ReasonTimeout => "Reading subtitle tracks timed out.",
        _ => "Unable to read embedded subtitle tracks."
    };
}
