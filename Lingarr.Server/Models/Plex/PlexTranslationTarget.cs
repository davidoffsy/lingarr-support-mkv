using Lingarr.Core.Enum;

namespace Lingarr.Server.Models.Plex;

/// <summary>
/// Hangfire-serializable description of one movie or episode to translate.
/// Library hits set <see cref="MediaId"/>; Plex-only titles leave it null and
/// identify the file by <see cref="Directory"/> + <see cref="FileName"/>.
/// </summary>
public sealed class PlexTranslationTarget
{
    public MediaType MediaType { get; set; }

    public int? MediaId { get; set; }

    public string Directory { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string? FileExtension { get; set; }

    public string DisplayTitle { get; set; } = string.Empty;

    public string? RatingKey { get; set; }

    public string? MappedFilePath { get; set; }
}
