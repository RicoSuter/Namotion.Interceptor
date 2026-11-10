using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class ReturnUrlsTests
{
    [Theory]
    [InlineData("/pages/Dashboard.md", "/pages/Dashboard.md")]
    [InlineData("/pages/Dashboard.md?tab=1#top", "/pages/Dashboard.md?tab=1#top")]
    [InlineData("/", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("\\\\evil.example/", "/")]
    [InlineData("/\t/evil.example/", "/")]
    [InlineData("pages/Dashboard.md", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void WhenAReturnUrlIsChecked_ThenOnlyLocalPathsAreKept(string? returnUrl, string expected)
    {
        // Act
        var safe = ReturnUrls.GetSafe(returnUrl, pathBase: "");

        // Assert
        Assert.Equal(expected, safe);
    }

    [Theory]
    [InlineData("/homeblaze/pages/Dashboard.md", "/homeblaze/pages/Dashboard.md")]
    [InlineData("/homeblaze", "/homeblaze")]
    [InlineData("/HomeBlaze/pages", "/HomeBlaze/pages")]
    [InlineData("/homeblazer/pages", "/homeblaze/")]
    [InlineData("/other/pages", "/homeblaze/")]
    [InlineData("//evil.example/", "/homeblaze/")]
    [InlineData(null, "/homeblaze/")]
    public void WhenAReturnUrlIsCheckedBelowAPathBase_ThenOnlyPathsBelowItAreKept(string? returnUrl, string expected)
    {
        // Act
        var safe = ReturnUrls.GetSafe(returnUrl, pathBase: "/homeblaze");

        // Assert
        Assert.Equal(expected, safe);
    }
}
