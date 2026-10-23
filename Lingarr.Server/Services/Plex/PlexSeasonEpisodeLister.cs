using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Services.Plex;

public class PlexSeasonEpisodeLister : IPlexSeasonEpisodeLister
{
    public const int PageSize = 200;
    private const int MaxPages = 50;

    private readonly ISettingService _settingService;
    private readonly IPlexApiClient _plexApiClient;
    private readonly ILogger<PlexSeasonEpisodeLister> _logger;

    public PlexSeasonEpisodeLister(
        ISettingService settingService,
        IPlexApiClient plexApiClient,
        ILogger<PlexSeasonEpisodeLister> logger)
    {
        _settingService = settingService;
        _plexApiClient = plexApiClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlexMetadata>> ListEpisodesAsync(
        string seasonRatingKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seasonRatingKey))
        {
            return [];
        }

        var plexUrl = await _settingService.GetSetting(SettingKeys.Integration.PlexUrl);
        var plexToken = await _settingService.GetEncryptedSetting(SettingKeys.Integration.PlexToken);
        if (string.IsNullOrWhiteSpace(plexUrl) || string.IsNullOrWhiteSpace(plexToken))
        {
            _logger.LogInformation(
                "Plex season expand skipped: Sign in with Plex required (season ratingKey {RatingKey})",
                seasonRatingKey);
            return [];
        }

        var items = new List<PlexMetadata>();
        var start = 0;
        for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            var page = await _plexApiClient.GetChildren(
                plexUrl,
                plexToken,
                seasonRatingKey,
                start,
                PageSize,
                cancellationToken);
            if (page == null)
            {
                if (start == 0)
                {
                    _logger.LogInformation(
                        "Plex season children not found (ratingKey {RatingKey})",
                        seasonRatingKey);
                }

                break;
            }

            items.AddRange(page.Items);
            if (page.Items.Count == 0)
            {
                break;
            }

            var pageOffset = page.Offset > 0 ? page.Offset : start;
            var nextStart = pageOffset + page.Items.Count;
            if (page.TotalSize > 0)
            {
                if (nextStart >= page.TotalSize)
                {
                    break;
                }
            }
            else if (page.Items.Count < PageSize)
            {
                break;
            }

            if (nextStart <= start)
            {
                break;
            }

            start = nextStart;
        }

        return items
            .Where(item => string.Equals(item.Type, "episode", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
