using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class MudBlazorIconResolverTests
{
    [Fact]
    public void WhenIconNameIsKnown_ThenTheMaterialFilledIconIsReturned()
    {
        // Act
        var icon = MudBlazorIconResolver.Resolve("Hub");

        // Assert
        Assert.Equal(Icons.Material.Filled.Hub, icon);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchIconName")]
    public void WhenIconNameIsMissingOrUnknown_ThenTheDefaultIconIsReturned(string? iconName)
    {
        // Act
        var icon = MudBlazorIconResolver.Resolve(iconName);

        // Assert
        Assert.Equal(Icons.Material.Filled.Article, icon);
    }
}
