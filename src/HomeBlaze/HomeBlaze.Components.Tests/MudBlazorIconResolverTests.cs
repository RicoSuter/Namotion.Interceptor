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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WhenIconNameOfAnOptionIsNotSet_ThenThereIsNoIcon(string? iconName)
    {
        // Act
        var icon = MudBlazorIconResolver.ResolveOrNull(iconName);

        // Assert
        Assert.Null(icon);
    }

    [Theory]
    [InlineData("Hub", Icons.Material.Filled.Hub)]
    [InlineData("NoSuchIconName", Icons.Material.Filled.Article)]
    public void WhenIconNameOfAnOptionIsSet_ThenItIsResolvedLikeAnyIconName(string iconName, string expected)
    {
        // Act
        var icon = MudBlazorIconResolver.ResolveOrNull(iconName);

        // Assert
        Assert.Equal(expected, icon);
    }
}
