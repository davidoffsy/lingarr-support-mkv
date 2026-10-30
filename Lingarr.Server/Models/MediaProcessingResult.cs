namespace Lingarr.Server.Models;

/// <summary>
/// Outcome of an automatic subtitle scan. <see cref="Retryable"/> means the
/// observation was incomplete (MKV missing or probe failed) and a later run
/// should try again without treating the current hash as final.
/// </summary>
public sealed class MediaProcessingResult
{
    public bool Translated { get; private init; }
    public bool Retryable { get; private init; }

    public static MediaProcessingResult Created() => new() { Translated = true };

    public static MediaProcessingResult Skipped() => new();

    public static MediaProcessingResult Retry() => new() { Retryable = true };
}
