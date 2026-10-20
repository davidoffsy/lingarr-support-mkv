using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services.Sync;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Sync;

public class MovieSyncTests
{
    [Theory]
    [InlineData("/movies/Dune.2021.MKV", ".mkv")]
    [InlineData("/movies/Dune.2021.mp4", ".mp4")]
    [InlineData("/movies/Dune", null)]
    [InlineData("", null)]
    public async Task SyncMovie_StoresNormalizedVideoExtension(string path, string? expected)
    {
        await using var db = CreateContext();
        var sync = CreateSync(db);

        var result = await sync.SyncMovie(CreateMovie(path), defaultInclude: false);

        Assert.NotNull(result);
        Assert.Equal(expected, result.FileExtension);
        Assert.Equal(string.IsNullOrEmpty(path) ? string.Empty : PathStem(path), result.FileName);
        Assert.False(result.IncludeInTranslation);
    }

    [Fact]
    public async Task SyncMovie_UpdatesExtensionWithoutChangingFileStem()
    {
        await using var db = CreateContext();
        db.Movies.Add(new Movie
        {
            RadarrId = 42,
            Title = "Old title",
            FileName = "Dune.2021",
            FileExtension = ".mkv",
            Path = "/movies",
            DateAdded = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var sync = CreateSync(db);

        var result = await sync.SyncMovie(CreateMovie("/movies/Dune.2021.mp4"), defaultInclude: true);

        Assert.NotNull(result);
        Assert.Equal(".mp4", result.FileExtension);
        Assert.Equal("Dune.2021", result.FileName);
        Assert.Equal("/movies", result.Path);
        Assert.Single(db.Movies);
    }

    private static MovieSync CreateSync(LingarrDbContext db) =>
        new(
            db,
            new PathConversionService(db),
            NullLogger<MovieSync>.Instance,
            new Mock<IImageSync>().Object);

    private static LingarrDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static RadarrMovie CreateMovie(string path) => new()
    {
        Id = 42,
        Title = "Dune",
        Path = "/movies/Dune.2021",
        RootFolderPath = "/movies",
        Added = "2024-01-01T00:00:00Z",
        HasFile = true,
        MovieFile = new RadarrMovieFile { Path = path },
        Images = null
    };

    private static string PathStem(string path)
    {
        var fileName = path.Split('/')[^1];
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 ? fileName[..^extension.Length] : fileName;
    }
}