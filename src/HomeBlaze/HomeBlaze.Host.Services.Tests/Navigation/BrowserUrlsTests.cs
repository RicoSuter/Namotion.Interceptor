using HomeBlaze.Host.Services.Navigation;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class BrowserUrlsTests
{
    [Theory]
    [InlineData("/Devices/Ecowitt.json", "/browser/Devices/Ecowitt.json")]
    [InlineData("/FritzBox/Clients/AA:BB:CC:00:11:22", "/browser/FritzBox/Clients/AA%3ABB%3ACC%3A00%3A11%3A22")]
    [InlineData("/Notes/A #1?/50%", "/browser/Notes/A%20%231%3F/50%25")]
    [InlineData("/", "/browser")]
    public void WhenARoutePathIsTurnedIntoAUrl_ThenEachSegmentIsEscaped(string routePath, string expected)
    {
        // Act
        var url = BrowserUrls.FromRoutePath(routePath);

        // Assert
        Assert.Equal(expected, url);
    }
}
