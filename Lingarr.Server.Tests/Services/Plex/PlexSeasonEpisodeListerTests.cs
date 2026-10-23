using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Services.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexSeasonEpisodeListerTests
{
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<IPlexApiClient> _plexApi = new();
    private readonly PlexSeasonEpisodeLister _lister;

    public PlexSeasonEpisodeListerTests()
    {
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.PlexUrl))
            .ReturnsAsync("http://plex:32400");
        _settings.Setup(s => s.GetEncryptedSetting(SettingKeys.Integration.PlexToken))
            .ReturnsAsync("plex-token");
        _lister = new PlexSeasonEpisodeLister(
            _settings.Object,
            _plexApi.Object,
            NullLogger<PlexSeasonEpisodeLister>.Instance);
    }

    [Fact]
    public async Task ListEpisodes_ThreeEpisodesAndSeasonChild_ReturnsEpisodesOnly()
    {
        _plexApi.Setup(c => c.GetChildren(
                "http://plex:32400",
                "plex-token",
                "55",
                0,
                PlexSeasonEpisodeLister.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexChildrenPage
            {
                Items =
                [
                    Episode("101", 1),
                    new PlexMetadata { Type = "season", RatingKey = "55", Title = "Season 1" },
                    Episode("102", 2),
                    Episode("103", 3)
                ],
                Size = 4,
                TotalSize = 4,
                Offset = 0
            });

        var episodes = await _lister.ListEpisodesAsync("55");

        Assert.Equal(new[] { "101", "102", "103" }, episodes.Select(item => item.RatingKey));
        _plexApi.Verify(
            c => c.GetChildren(
                "http://plex:32400",
                "plex-token",
                "55",
                0,
                PlexSeasonEpisodeLister.PageSize,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListEpisodes_PaginatesUntilTotalSize()
    {
        var firstPage = Enumerable.Range(1, PlexSeasonEpisodeLister.PageSize)
            .Select(index => Episode(index.ToString(), index))
            .ToList();
        _plexApi.Setup(c => c.GetChildren(
                "http://plex:32400",
                "plex-token",
                "55",
                0,
                PlexSeasonEpisodeLister.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexChildrenPage
            {
                Items = firstPage,
                Size = PlexSeasonEpisodeLister.PageSize,
                TotalSize = PlexSeasonEpisodeLister.PageSize + 1,
                Offset = 0
            });
        _plexApi.Setup(c => c.GetChildren(
                "http://plex:32400",
                "plex-token",
                "55",
                PlexSeasonEpisodeLister.PageSize,
                PlexSeasonEpisodeLister.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexChildrenPage
            {
                Items = [Episode("201", 201)],
                Size = 1,
                TotalSize = PlexSeasonEpisodeLister.PageSize + 1,
                Offset = PlexSeasonEpisodeLister.PageSize
            });

        var episodes = await _lister.ListEpisodesAsync("55");

        Assert.Equal(PlexSeasonEpisodeLister.PageSize + 1, episodes.Count);
        Assert.Equal("201", episodes[^1].RatingKey);
        _plexApi.Verify(
            c => c.GetChildren(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "55",
                It.IsAny<int>(),
                PlexSeasonEpisodeLister.PageSize,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ListEpisodes_NotFound_ReturnsEmpty()
    {
        _plexApi.Setup(c => c.GetChildren(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "55",
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlexChildrenPage?)null);

        var episodes = await _lister.ListEpisodesAsync("55");

        Assert.Empty(episodes);
    }

    [Fact]
    public async Task ListEpisodes_EmptySeason_ReturnsEmpty()
    {
        _plexApi.Setup(c => c.GetChildren(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "55",
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexChildrenPage
            {
                Items = [],
                Size = 0,
                TotalSize = 0,
                Offset = 0
            });

        var episodes = await _lister.ListEpisodesAsync("55");

        Assert.Empty(episodes);
    }

    [Fact]
    public async Task ListEpisodes_NotSignedIn_DoesNotCallPlex()
    {
        _settings.Setup(s => s.GetEncryptedSetting(SettingKeys.Integration.PlexToken))
            .ReturnsAsync(string.Empty);

        var episodes = await _lister.ListEpisodesAsync("55");

        Assert.Empty(episodes);
        _plexApi.Verify(
            c => c.GetChildren(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListEpisodes_MissingRatingKey_ReturnsEmpty()
    {
        var episodes = await _lister.ListEpisodesAsync(" ");

        Assert.Empty(episodes);
        _plexApi.Verify(
            c => c.GetChildren(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static PlexMetadata Episode(string ratingKey, int index) =>
        new()
        {
            Type = "episode",
            RatingKey = ratingKey,
            Title = $"Episode {index}",
            ParentIndex = 1,
            Index = index,
            GrandparentTitle = "Orphan",
            File = $"/mnt/plex/Orphan/S01E{index:00}.mkv"
        };
}
