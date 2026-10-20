using System;
using System.IO;
using Lingarr.Core.Entities;
using Lingarr.Server.Services.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Services.Media;

public class MediaFileLocatorTests : IDisposable
{
    private readonly string _directory;
    private readonly MediaFileLocator _locator = new(NullLogger<MediaFileLocator>.Instance);

    public MediaFileLocatorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lingarr-mkv-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void FindMkv_ReturnsDirectPathWhenFileExists()
    {
        var path = Touch("Dune.2021.mkv");

        var result = _locator.FindMkv(_directory, "Dune.2021");

        Assert.Equal(path, result);
    }

    [Fact]
    public void FindMkv_FallsBackToStemPrefixedMkv()
    {
        var path = Touch("Show.S01E01.1080p.mkv");

        var result = _locator.FindMkv(_directory, "Show.S01E01");

        Assert.Equal(path, result);
    }

    [Fact]
    public void FindMkv_IgnoresNonMkvFiles()
    {
        Touch("Dune.2021.mp4");
        Touch("Dune.2021.en.srt");

        var result = _locator.FindMkv(_directory, "Dune.2021");

        Assert.Null(result);
    }

    [Fact]
    public void FindMkv_PrefersExactStemOverLongerMatch()
    {
        var exact = Touch("Movie.mkv");
        Touch("Movie.extended.mkv");

        var result = _locator.FindMkv(_directory, "Movie");

        Assert.Equal(exact, result);
    }

    [Fact]
    public void FindMkv_ReturnsNullWhenDirectoryOrFileNameMissing()
    {
        Assert.Null(_locator.FindMkv(null, "Movie"));
        Assert.Null(_locator.FindMkv(_directory, null));
        Assert.Null(_locator.FindMkv("/does/not/exist", "Movie"));
    }

    [Fact]
    public void FindMkv_UsesMediaPathAndFileName()
    {
        var path = Touch("Episode.mkv");
        var media = new Movie
        {
            RadarrId = 1,
            Title = "Episode",
            FileName = "Episode",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };

        var result = _locator.FindMkv(media);

        Assert.Equal(path, result);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".MP4")]
    [InlineData(".avi")]
    public void FindMkv_KnownNonMkvMediaDoesNotMatchAnotherMkv(string extension)
    {
        Touch("Movie.mkv");
        Touch("Movie.extended.mkv");
        Touch("Movie" + extension);
        var media = new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            FileName = "Movie",
            FileExtension = extension,
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };

        Assert.Null(_locator.FindMkv(media));
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".MKV")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void FindMkv_KnownMkvOrUnknownMediaRetainsLookup(string? extension)
    {
        var path = Touch("Movie.mkv");
        var media = new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            FileName = "Movie",
            FileExtension = extension,
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };

        Assert.Equal(path, _locator.FindMkv(media));
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, [0x1]);
        return path;
    }
}
