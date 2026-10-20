using HomeBlaze.Host.Services.Navigation;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class NavigationItemTests
{
    [Theory]
    [InlineData("pages/My Page.md", true)]
    [InlineData("pages/My%20Page.md", true)]
    [InlineData("pages/my%20page.md", true)]
    [InlineData("pages/My%20Page.md?edit=true", true)]
    [InlineData("pages/My%20Page.md#section", true)]
    [InlineData("pages/My%20Page.md?edit=true#section", true)]
    [InlineData("pages/Other.md", false)]
    [InlineData("pages/My%20Page.md/child", false)]
    public void WhenTheUrlIsCompared_ThenItsEscapingQueryAndFragmentDoNotMatter(string baseRelativeUrl, bool expected)
    {
        // Arrange
        var item = new NavigationItem
        {
            Subject = new NavigationTestPage(InterceptorSubjectContext.Create()),
            Title = "My Page",
            Icon = "Article",
            Path = "/My Page.md"
        };

        // Act
        var result = item.IsPageUrlOf(baseRelativeUrl);

        // Assert
        Assert.Equal(expected, result);
    }
}
