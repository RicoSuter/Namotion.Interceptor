using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class MudBlazorIconResolverTests
{
    [Theory]
    [InlineData("Hub", Icons.Material.Filled.Hub)]
    [InlineData("MenuBook", Icons.Material.Filled.MenuBook)]
    [InlineData("Folder", Icons.Material.Filled.Folder)]
    public void WhenIconNameIsKnown_ThenTheMaterialFilledIconIsReturned(string iconName, string expected)
    {
        // Act
        var icon = MudBlazorIconResolver.Resolve(iconName);

        // Assert
        Assert.Equal(expected, icon);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchIconName")]
    [InlineData("hub")]
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
