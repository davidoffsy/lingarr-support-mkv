namespace Lingarr.Server.Services.Webhooks;

/// <summary>
/// Delayed Arr webhook translation: first scan after 30s, then 2min and 10min
/// if the MKV still cannot be observed. Attempt is 0-based.
/// </summary>
public static class ArrWebhookTranslationRetry
{
    public const int MaxAttempts = 3;

    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AfterFirstFailure = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AfterSecondFailure = TimeSpan.FromMinutes(10);

    public static TimeSpan? NextDelay(int completedAttempt) => completedAttempt switch
    {
        0 => AfterFirstFailure,
        1 => AfterSecondFailure,
        _ => null
    };
}
