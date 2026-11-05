using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class AuthorizationDisplayTests
{
    [Fact]
    public void WhenARoleIsHeldByAnAuthorization_ThenItsTitleIsItsKey()
    {
        // Arrange
        var role = new Role();
        var authorization = new Authorization(TestContext.Create())
        {
            Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["kitchen"] = role }
        };

        // Act
        var title = ((ITitleProvider)role).Title;

        // Assert
        Assert.Equal("kitchen", title);
        Assert.Same(authorization, role.FindParent().Authorization);
    }

    [Fact]
    public void WhenARoleIsHeldByNoAuthorization_ThenItHasNoTitle()
    {
        // Arrange
        var role = new Role(TestContext.Create());

        // Act
        var title = ((ITitleProvider)role).Title;

        // Assert
        Assert.Null(title);
    }

    [Fact]
    public void WhenTheIconsAreRead_ThenTheyAreTheIconsTheTypePickerShows()
    {
        // Act
        var authorizationIcon = ((IIconProvider)new Authorization()).IconName;
        var roleIcon = ((IIconProvider)new Role()).IconName;

        // Assert
        Assert.Equal("AdminPanelSettings", authorizationIcon);
        Assert.Equal("Badge", roleIcon);
        Assert.Equal(typeof(Authorization).GetCustomAttribute<SubjectIconAttribute>()!.IconName, authorizationIcon);
        Assert.Equal(typeof(Role).GetCustomAttribute<SubjectIconAttribute>()!.IconName, roleIcon);
    }
}
