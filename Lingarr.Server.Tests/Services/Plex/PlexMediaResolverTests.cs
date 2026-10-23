using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Integration;
using Lingarr.Server.Interfaces.Services.Plex;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Plex;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexMediaResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _db;
    private readonly PathConversionService _paths;
    private readonly Mock<ISettingService> _settings = new();
    private readonly Mock<IPlexApiClient> _plexApi = new();
    private readonly Mock<IMediaService> _media = new();
    private readonly Mock<IRadarrService> _radarr = new();
    private readonly Mock<ISonarrService> _sonarr = new();
    private readonly PlexMediaResolver _resolver;

    public PlexMediaResolverTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new LingarrDbContext(options);
        _db.Database.EnsureCreated();
        _paths = new PathConversionService(_db);

        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.PlexUrl))
            .ReturnsAsync("http://plex:32400");
        _settings.Setup(s => s.GetEncryptedSetting(SettingKeys.Integration.PlexToken))
            .ReturnsAsync("plex-token");

        _resolver = new PlexMediaResolver(
            _settings.Object,
            _plexApi.Object,
            _paths,
            _db,
            _media.Object,
            _radarr.Object,
            _sonarr.Object,
            NullLogger<PlexMediaResolver>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Resolve_MoviePathMapping_HitsExistingMovie()
    {
        _db.PathMappings.Add(new PathMapping
        {
            SourcePath = "/mnt/plex",
            DestinationPath = "/movies",
            MediaType = MediaType.Movie
        });
        _db.Movies.Add(CreateMovie(10, "Foo", "/movies/Foo", "Foo"));
        await _db.SaveChangesAsync();

        _plexApi.Setup(c => c.GetMetadata(
                "http://plex:32400",
                "plex-token",
                "42",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "movie",
                Title = "Foo",
                File = "/mnt/plex/Foo/Foo.mkv",
                Guids = []
            });

        var result = await _resolver.ResolveAsync(MoviePayload("42", "Foo"));

        Assert.True(result.Found);
        Assert.Equal(MediaType.Movie, result.MediaType);
        Assert.Equal("/movies/Foo/Foo.mkv", result.MappedPath);
        var movie = Assert.Single(_db.Movies);
        Assert.Equal(movie.Id, result.EntityId);
        _radarr.Verify(r => r.GetMovies(), Times.Never);
        _media.Verify(m => m.GetMovieIdOrSyncFromRadarrMovieId(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_EpisodeUsesShowMapping_MatchesEpisodeOrSeasonPath()
    {
        _db.PathMappings.Add(new PathMapping
        {
            SourcePath = "/mnt/plex",
            DestinationPath = "/shows",
            MediaType = MediaType.Show
        });
        var show = CreateShow(1, "Bar", "/shows/Bar");
        var season = new Season
        {
            SeasonNumber = 1,
            Path = "/shows/Bar/Season 01",
            Show = show
        };
        _db.Episodes.Add(new Episode
        {
            SonarrId = 5,
            EpisodeNumber = 1,
            Title = "Pilot",
            FileName = "Bar - S01E01",
            Path = "/shows/Bar/Season 01/other",
            Season = season
        });
        await _db.SaveChangesAsync();

        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "7",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "episode",
                Title = "Pilot",
                GrandparentTitle = "Bar",
                ParentIndex = 1,
                Index = 1,
                File = "/mnt/plex/Bar/Season 01/Bar - S01E01.mkv",
                Guids = []
            });

        var result = await _resolver.ResolveAsync(EpisodePayload("7", "Pilot"));

        Assert.True(result.Found);
        Assert.Equal(MediaType.Episode, result.MediaType);
        Assert.Equal("/shows/Bar/Season 01/Bar - S01E01.mkv", result.MappedPath);
        Assert.Equal(Assert.Single(_db.Episodes).Id, result.EntityId);
        _sonarr.Verify(s => s.GetShows(), Times.Never);
    }

    [Fact]
    public async Task Resolve_EpisodePathOnEpisodeEntity_Hits()
    {
        _db.PathMappings.Add(new PathMapping
        {
            SourcePath = "/mnt/plex",
            DestinationPath = "/shows",
            MediaType = MediaType.Show
        });
        var show = CreateShow(1, "Bar", "/shows/Bar");
        var season = new Season
        {
            SeasonNumber = 1,
            Path = "/shows/Bar",
            Show = show
        };
        _db.Episodes.Add(new Episode
        {
            SonarrId = 5,
            EpisodeNumber = 2,
            Title = "Next",
            FileName = "Bar - S01E02",
            Path = "/shows/Bar/Season 01",
            Season = season
        });
        await _db.SaveChangesAsync();

        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "8",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "episode",
                Title = "Next",
                ParentIndex = 1,
                Index = 2,
                File = "/mnt/plex/Bar/Season 01/Bar - S01E02.mkv",
                Guids = []
            });

        var result = await _resolver.ResolveAsync(EpisodePayload("8", "Next"));

        Assert.True(result.Found);
        Assert.Equal(Assert.Single(_db.Episodes).Id, result.EntityId);
    }

    [Fact]
    public async Task Resolve_PathMiss_GuidTmdbHitsRadarrSyncLookup()
    {
        _db.PathMappings.Add(new PathMapping
        {
            SourcePath = "/mnt/plex",
            DestinationPath = "/movies",
            MediaType = MediaType.Movie
        });
        await _db.SaveChangesAsync();

        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "42",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "movie",
                Title = "Foo",
                File = "/mnt/plex/Unknown/Unknown.mkv",
                Guids = ["tmdb://12345", "plex://movie/abc"]
            });
        _radarr.Setup(r => r.GetMovieByTmdbId(12345))
            .ReturnsAsync(CreateRadarrMovie(99));
        _media.Setup(m => m.GetMovieIdOrSyncFromRadarrMovieId(99))
            .ReturnsAsync(77);

        var result = await _resolver.ResolveAsync(MoviePayload("42", "Foo"));

        Assert.True(result.Found);
        Assert.Equal(77, result.EntityId);
        Assert.Equal(MediaType.Movie, result.MediaType);
        _media.Verify(m => m.GetMovieIdOrSyncFromRadarrMovieId(99), Times.Once);
        _radarr.Verify(r => r.GetMovies(), Times.Never);
        Assert.Empty(_db.Movies);
    }

    [Fact]
    public async Task Resolve_PathMissAndGuidMiss_DoesNotInsert()
    {
        _db.Movies.Add(CreateMovie(1, "Existing", "/movies/Existing", "Existing"));
        await _db.SaveChangesAsync();

        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "42",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "movie",
                Title = "Unknown",
                File = "/other/Unknown.mkv",
                Guids = ["tmdb://999", "imdb://tt000"]
            });
        _radarr.Setup(r => r.GetMovieByTmdbId(999)).ReturnsAsync((RadarrMovie?)null);
        _radarr.Setup(r => r.GetMovieByImdbId("tt000")).ReturnsAsync((RadarrMovie?)null);

        var result = await _resolver.ResolveAsync(MoviePayload("42", "Unknown"));

        Assert.True(result.Found);
        Assert.True(result.IsPathOnly);
        Assert.Equal(0, result.EntityId);
        Assert.Equal("/other/Unknown.mkv", result.MappedPath);
        Assert.Equal("Unknown", result.Title);
        Assert.Equal(1, _db.Movies.Count());
        Assert.Empty(_db.ChangeTracker.Entries<Movie>().Where(e => e.State == EntityState.Added));
        _media.Verify(m => m.GetMovieIdOrSyncFromRadarrMovieId(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_PathOnlyMovie_UsesYearInDisplayTitle()
    {
        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "42",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "movie",
                Title = "Dune",
                Year = 2021,
                File = "/mnt/orphan/Dune/Dune.mkv",
                Guids = []
            });

        var result = await _resolver.ResolveAsync(MoviePayload("42", "Dune"));

        Assert.True(result.IsPathOnly);
        Assert.Equal("Dune (2021)", result.Title);
        Assert.Equal("/mnt/orphan/Dune/Dune.mkv", result.MappedPath);
        Assert.Empty(_db.Movies);
    }

    [Fact]
    public async Task Resolve_NoTokenAndNoFile_SkipsWithSignInReason()
    {
        _settings.Setup(s => s.GetSetting(SettingKeys.Integration.PlexUrl))
            .ReturnsAsync("");
        _settings.Setup(s => s.GetEncryptedSetting(SettingKeys.Integration.PlexToken))
            .ReturnsAsync("");

        var result = await _resolver.ResolveAsync(MoviePayload("42", "Foo"));

        Assert.False(result.Found);
        Assert.Contains("Sign in", result.SkipReason, StringComparison.OrdinalIgnoreCase);
        _plexApi.Verify(
            c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Resolve_Plex404_SkipsWithoutThrowing()
    {
        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "404",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlexMetadata?)null);

        var result = await _resolver.ResolveAsync(MoviePayload("404", "Missing"));

        Assert.False(result.Found);
        Assert.Contains("metadata not found", result.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_TitleOnlyPayload_DoesNotMatchDifferentlyNamedMovie()
    {
        _db.Movies.Add(CreateMovie(1, "The Matrix", "/movies/The Matrix", "The Matrix"));
        await _db.SaveChangesAsync();

        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "movie",
                Title = "Inception",
                File = null,
                Guids = []
            });

        var result = await _resolver.ResolveAsync(MoviePayload("1", "Inception"));

        Assert.False(result.Found);
        Assert.Equal(1, _db.Movies.Count());
        _media.Verify(m => m.GetMovieIdOrSyncFromRadarrMovieId(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_EpisodeGuid_DoesNotTreatEpisodeTvdbAsSeriesId()
    {
        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "7",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "episode",
                Title = "Pilot",
                ParentIndex = 1,
                Index = 1,
                File = "/no/match.mkv",
                Guids = ["tvdb://999001", "imdb://tt999001"]
            });

        var result = await _resolver.ResolveAsync(EpisodePayload("7", "Pilot"));

        Assert.True(result.IsPathOnly);
        Assert.Equal(0, result.EntityId);
        Assert.Empty(_db.Episodes);
        _sonarr.Verify(s => s.GetShowByTvdbId(It.IsAny<int>()), Times.Never);
        _sonarr.Verify(s => s.GetShowByImdbId(It.IsAny<string>()), Times.Never);
        _media.Verify(m => m.GetEpisodeIdOrSyncFromSonarrEpisodeId(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_EpisodePathMiss_UsesShowGuidsFromGrandparent()
    {
        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "7",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "episode",
                Title = "Pilot",
                GrandparentRatingKey = "show-1",
                ParentIndex = 1,
                Index = 2,
                File = "/no/match.mkv",
                Guids = ["tvdb://999001", "imdb://tt999001"]
            });
        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "show-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "show",
                Title = "Bar",
                Guids = ["tvdb://81189", "imdb://tt0903747"]
            });
        _sonarr.Setup(s => s.GetShowByTvdbId(81189))
            .ReturnsAsync(CreateSonarrShow(40, 81189));
        _sonarr.Setup(s => s.GetEpisodes(40, 1))
            .ReturnsAsync([
                new SonarrEpisode
                {
                    Id = 88,
                    EpisodeNumber = 2,
                    Title = "Pilot",
                    SeasonNumber = 1,
                    HasFile = true
                }
            ]);
        _media.Setup(m => m.GetEpisodeIdOrSyncFromSonarrEpisodeId(88))
            .ReturnsAsync(77);

        var result = await _resolver.ResolveAsync(EpisodePayload("7", "Pilot"));

        Assert.True(result.Found);
        Assert.Equal(MediaType.Episode, result.MediaType);
        Assert.Equal(77, result.EntityId);
        _sonarr.Verify(s => s.GetShowByTvdbId(81189), Times.Once);
        _sonarr.Verify(s => s.GetShowByTvdbId(999001), Times.Never);
        _sonarr.Verify(s => s.GetShowByImdbId("tt999001"), Times.Never);
    }

    [Fact]
    public async Task Resolve_GuidHttpFailure_Throws()
    {
        _plexApi.Setup(c => c.GetMetadata(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "42",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlexMetadata
            {
                Type = "movie",
                Title = "Foo",
                File = "/no/match.mkv",
                Guids = ["tmdb://1"]
            });
        _radarr.Setup(r => r.GetMovieByTmdbId(1))
            .ThrowsAsync(new HttpRequestException("Radarr down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => _resolver.ResolveAsync(MoviePayload("42", "Foo")));
    }

    private static PlexWebhookPayload MoviePayload(string ratingKey, string title) =>
        new()
        {
            Event = "media.rate",
            Metadata = new PlexWebhookMetadata
            {
                Type = "movie",
                Title = title,
                RatingKey = ratingKey
            }
        };

    private static PlexWebhookPayload EpisodePayload(string ratingKey, string title) =>
        new()
        {
            Event = "media.rate",
            Metadata = new PlexWebhookMetadata
            {
                Type = "episode",
                Title = title,
                RatingKey = ratingKey
            }
        };

    private static Movie CreateMovie(int radarrId, string title, string path, string fileName) =>
        new()
        {
            RadarrId = radarrId,
            Title = title,
            Path = path,
            FileName = fileName,
            DateAdded = DateTimeOffset.UtcNow
        };

    private static Show CreateShow(int sonarrId, string title, string path) =>
        new()
        {
            SonarrId = sonarrId,
            Title = title,
            Path = path,
            DateAdded = DateTimeOffset.UtcNow
        };

    private static RadarrMovie CreateRadarrMovie(int id) =>
        new()
        {
            Id = id,
            Title = "Arr",
            Path = "/movies/Arr",
            RootFolderPath = "/movies",
            Added = DateTime.UtcNow.ToString("o"),
            HasFile = true,
            TmdbId = 12345
        };

    private static SonarrShow CreateSonarrShow(int id, int tvdbId) =>
        new()
        {
            Id = id,
            Title = "Bar",
            Path = "/shows/Bar",
            Added = DateTime.UtcNow.ToString("o"),
            SeasonFolder = true,
            TvdbId = tvdbId
        };
}
