using HomeBlaze.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectTypeCatalogGroupIconTests
{
    [Fact]
    public void WhenGroupIsBuiltIn_ThenItsIconNameResolves()
    {
        // Arrange
        var fallbackIcon = MudBlazorIconResolver.Resolve(null);

        // Act
        var unresolvedIcons = SubjectTypeCatalog.GroupIconNames
            .Where(groupIcon => groupIcon.Value != "Article" &&
                                MudBlazorIconResolver.Resolve(groupIcon.Value) == fallbackIcon)
            .Select(groupIcon => $"{groupIcon.Key}: {groupIcon.Value}")
            .ToList();

        // Assert - an unknown name silently shows the fallback icon instead of failing.
        Assert.NotEmpty(SubjectTypeCatalog.GroupIconNames);
        Assert.Empty(unresolvedIcons);
    }
}
