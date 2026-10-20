using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services.Integration;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Sync;

public class EpisodeSyncTests
{
    [Theory]
    [InlineData("/tv/Show/S01E01.MKV", ".mkv")]
    [InlineData("/tv/Show/S01E01.mp4", ".mp4")]
    [InlineData("/tv/Show/S01E01", null)]
    [InlineData("", null)]
    public async Task SyncEpisodes_StoresNormalizedVideoExtension(string path, string? expected)
    {
        await using var db = CreateContext();
        var season = CreateSeason();
        var sync = CreateSync(db, path, CreateEpisode(1, 1));

        await sync.SyncEpisodes(CreateShow(), season, defaultInclude: false);

        var episode = Assert.Single(season.Episodes);
        Assert.Equal(expected, episode.FileExtension);
        Assert.Equal(string.IsNullOrEmpty(path) ? string.Empty : "S01E01", episode.FileName);
        Assert.False(episode.IncludeInTranslation);
    }

    [Fact]
    public async Task SyncEpisodes_UpdatesExtensionWithoutChangingFileStem()
    {
        await using var db = CreateContext();
        var season = CreateSeason();
        season.Episodes.Add(new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "Old title",
            FileName = "S01E01",
            FileExtension = ".mkv",
            Path = "/tv/Show",
            Season = season,
            DateAdded = DateTimeOffset.UtcNow
        });
        var sync = CreateSync(db, "/tv/Show/S01E01.mp4", CreateEpisode(1, 1));

        await sync.SyncEpisodes(CreateShow(), season, defaultInclude: true);

        var episode = Assert.Single(season.Episodes);
        Assert.Equal(".mp4", episode.FileExtension);
        Assert.Equal("S01E01", episode.FileName);
        Assert.Equal("/tv/Show", episode.Path);
    }

    private static EpisodeSync CreateSync(LingarrDbContext db, string path, params SonarrEpisode[] episodes)
    {
        var sonarr = new Mock<ISonarrService>();
        sonarr.Setup(s => s.GetEpisodes(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(episodes.ToList());
        foreach (var episode in episodes)
        {
            sonarr.Setup(s => s.GetEpisodePath(episode.Id))
                .ReturnsAsync(new SonarrEpisodePath
                {
                    EpisodeFile = new SonarrEpisodeFile
                    {
                        Path = path
                    }
                });
        }

        return new EpisodeSync(sonarr.Object, new PathConversionService(db));
    }

    private static SonarrEpisode CreateEpisode(int id, int number) => new()
    {
        Id = id,
        EpisodeNumber = number,
        Title = "Episode",
        SeasonNumber = 1,
        HasFile = true
    };

    private static LingarrDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SonarrShow CreateShow() => new()
    {
        Id = 1,
        Title = "Show",
        Path = "/tv/Show",
        Added = "2024-01-01T00:00:00Z",
        SeasonFolder = false
    };

    private static Season CreateSeason() => new()
    {
        SeasonNumber = 1,
        Path = "/tv/Show",
        Show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = "/tv/Show",
            DateAdded = DateTimeOffset.UtcNow
        }
    };
}