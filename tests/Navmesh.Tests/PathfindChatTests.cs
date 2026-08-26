using System.Globalization;
using System.Numerics;
using Navmesh.Movement.Human;
using Xunit;

namespace Navmesh.Tests;

public class PathfindChatTests
{
    [Fact]
    public void SearchingNamesTheDestination()
    {
        Assert.Equal("searching for path to 12.3, -5.0, 78.9", PathfindChat.Searching(new Vector3(12.34f, -5f, 78.9f), false, 0));
    }

    [Fact]
    public void FlyingSaysSo()
    {
        Assert.Equal("searching for flight path to 0.0, 0.0, 0.0", PathfindChat.Searching(Vector3.Zero, true, 0));
    }

    [Fact]
    public void ToleranceIsMentionedOnlyWhenThereIsOne()
    {
        Assert.Equal("searching for path to 1.0, 2.0, 3.0 within 4.5y", PathfindChat.Searching(new Vector3(1, 2, 3), false, 4.5f));
        Assert.DoesNotContain("within", PathfindChat.Searching(new Vector3(1, 2, 3), false, 0));
    }

    [Fact]
    public void NumbersReadTheSameUnderEveryCulture()
    {
        // a locale with a comma decimal separator would otherwise turn "1.5, 2.0, 3.0" into
        // "1,5, 2,0, 3,0", which cannot be read back as three coordinates
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
            Assert.Equal("searching for path to 1.5, 2.0, 3.0", PathfindChat.Searching(new Vector3(1.5f, 2, 3), false, 0));
            Assert.Equal("done in 1.250 seconds", PathfindChat.Done(TimeSpan.FromSeconds(1.25)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DoneReportsMillisecondsAndKeepsTrailingZeroes()
    {
        Assert.Equal("done in 0.008 seconds", PathfindChat.Done(TimeSpan.FromMilliseconds(7.6)));
        Assert.Equal("done in 13.100 seconds", PathfindChat.Done(TimeSpan.FromSeconds(13.1)));
    }

    [Fact]
    public void GivingUpIsShort()
    {
        Assert.Equal("cancelled", PathfindChat.Cancelled());
        Assert.Equal("failed", PathfindChat.Failed());
    }
}
