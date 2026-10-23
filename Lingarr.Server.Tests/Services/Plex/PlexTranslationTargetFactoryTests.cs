using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.Plex;
using Lingarr.Server.Services.Plex;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexTranslationTargetFactoryTests
{
    [Fact]
    public async Task TryCreate_LibraryHitWithMappedPath_UsesLibraryPathNotPlexPath()
    {
        await using var db = CreateDb();
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Foo",
            Path = "/data/movies/Foo",
            FileName = "Foo.2021",
            FileExtension = ".mkv",
            DateAdded = System.DateTimeOffset.UtcNow
        };
        db.Movies.Add(movie);
        await db.SaveChangesAsync();

        var factory = new PlexTranslationTargetFactory(db);
        var resolved = PlexMediaResolveResult.Hit(
            MediaType.Movie,
            movie.Id,
            "/mnt/plex/movies/Foo/Foo.mkv",
            "42",
            "Foo (2021)");

        var target = await factory.TryCreateAsync(resolved);

        Assert.NotNull(target);
        Assert.Equal(movie.Id, target!.MediaId);
        Assert.Equal("/data/movies/Foo", target.Directory);
        Assert.Equal("Foo.2021", target.FileName);
        Assert.Equal(".mkv", target.FileExtension);
        Assert.Equal("Foo (2021)", target.DisplayTitle);
        Assert.Equal("42", target.RatingKey);
        Assert.Equal("/mnt/plex/movies/Foo/Foo.mkv", target.MappedFilePath);
    }

    [Fact]
    public async Task TryCreate_LibraryHitMissingRow_ReturnsNullEvenWithMappedPath()
    {
        await using var db = CreateDb();
        var factory = new PlexTranslationTargetFactory(db);
        var resolved = PlexMediaResolveResult.Hit(
            MediaType.Movie,
            9,
            "/movies/Foo/Foo.mkv",
            "42",
            "Foo (2021)");

        var target = await factory.TryCreateAsync(resolved);

        Assert.Null(target);
    }

    [Fact]
    public async Task TryCreate_PathHit_LeavesMediaIdNull()
    {
        await using var db = CreateDb();
        var factory = new PlexTranslationTargetFactory(db);
        var resolved = PlexMediaResolveResult.PathHit(
            MediaType.Episode,
            "/shows/Bar/Season 01/Bar - S01E02.mkv",
            "7",
            "Bar - S01E02 - Pilot");

        var target = await factory.TryCreateAsync(resolved);

        Assert.NotNull(target);
        Assert.Null(target!.MediaId);
        Assert.Equal(MediaType.Episode, target.MediaType);
        Assert.Equal("/shows/Bar/Season 01", target.Directory);
        Assert.Equal("Bar - S01E02", target.FileName);
        Assert.Equal("Bar - S01E02 - Pilot", target.DisplayTitle);
        Assert.Empty(db.Movies);
        Assert.Empty(db.Episodes);
    }

    [Fact]
    public async Task TryCreate_LibraryHitWithoutMappedPath_LoadsMediaRow()
    {
        await using var db = CreateDb();
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Foo",
            Path = "/movies/Foo",
            FileName = "Foo",
            FileExtension = ".mkv",
            DateAdded = System.DateTimeOffset.UtcNow
        };
        db.Movies.Add(movie);
        await db.SaveChangesAsync();

        var factory = new PlexTranslationTargetFactory(db);
        var resolved = PlexMediaResolveResult.Hit(MediaType.Movie, movie.Id, null, "42", "Foo");

        var target = await factory.TryCreateAsync(resolved);

        Assert.NotNull(target);
        Assert.Equal(movie.Id, target!.MediaId);
        Assert.Equal("/movies/Foo", target.Directory);
        Assert.Equal("Foo", target.FileName);
        Assert.Equal(".mkv", target.FileExtension);
    }

    [Fact]
    public async Task TryCreate_Miss_ReturnsNull()
    {
        await using var db = CreateDb();
        var factory = new PlexTranslationTargetFactory(db);

        var target = await factory.TryCreateAsync(
            PlexMediaResolveResult.Miss("not in Lingarr library"),
            CancellationToken.None);

        Assert.Null(target);
    }

    private static LingarrDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase("plex-target-factory-" + System.Guid.NewGuid())
            .Options;
        return new LingarrDbContext(options);
    }
}
