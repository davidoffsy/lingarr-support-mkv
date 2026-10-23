using Lingarr.Server.Services.Plex;
using Xunit;

namespace Lingarr.Server.Tests.Services.Plex;

public class PlexDisplayTitleTests
{
    [Fact]
    public void Movie_WithYear_AppendsYear()
    {
        Assert.Equal("Dune (2021)", PlexDisplayTitle.Movie("Dune", 2021));
    }

    [Fact]
    public void Movie_YearAlreadyInTitle_DoesNotDuplicate()
    {
        Assert.Equal("Dune (2021)", PlexDisplayTitle.Movie("Dune (2021)", 2021));
    }

    [Fact]
    public void Movie_NoYear_UsesTitle()
    {
        Assert.Equal("Dune", PlexDisplayTitle.Movie("Dune", null));
    }

    [Fact]
    public void Episode_FormatsShowSeasonAndTitle()
    {
        Assert.Equal(
            "Bar - S01E02 - Pilot",
            PlexDisplayTitle.Episode("Bar", 1, 2, "Pilot"));
    }

    [Fact]
    public void Episode_MissingSeason_FallsBackToShowAndTitle()
    {
        Assert.Equal("Bar - Pilot", PlexDisplayTitle.Episode("Bar", null, 2, "Pilot"));
    }
}
