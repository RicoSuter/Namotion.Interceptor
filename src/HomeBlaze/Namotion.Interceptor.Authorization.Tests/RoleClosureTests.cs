using Namotion.Interceptor.Authorization.Internal;

namespace Namotion.Interceptor.Authorization.Tests;

public class RoleClosureTests
{
    private static AuthorizationRole Role(string name, params string[] includes) => new() { Name = name, Includes = [.. includes] };

    [Fact]
    public void WhenRolesIncludeEachOtherInADiamond_ThenEveryRoleIsResolvedOnce()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Base"), Role("Left", "Base"), Role("Right", "Base"), Role("Top", "Left", "Right")];

        // Act
        var closure = RoleClosure.Resolve(roles, ["Top"]);

        // Assert
        Assert.Equal(["Base", "Left", "Right", "Top"], closure.Select(role => role.Name));
    }

    [Fact]
    public void WhenRoleNamesDifferInCase_ThenTheyResolveToTheSameRole()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Reader"), Role("Agent", "READER")];

        // Act
        var closure = RoleClosure.Resolve(roles, ["agent"]);

        // Assert
        Assert.Equal(["Reader", "Agent"], closure.Select(role => role.Name));
    }

    [Fact]
    public void WhenARoleNameIsUnknown_ThenResolveThrows()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Agent", "Missing")];

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, ["Agent"]));

        // Assert
        Assert.Equal("Role 'Agent' includes 'Missing', which is not defined.", exception.Message);
    }

    [Fact]
    public void WhenRolesFormACycle_ThenResolveThrowsNamingThePath()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("A", "B"), Role("B", "C"), Role("C", "A")];

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, ["A"]));

        // Assert
        Assert.Equal("Role 'A' includes itself: A > B > C > A.", exception.Message);
    }

    [Fact]
    public void WhenTwoRolesShareAName_ThenResolveThrows()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Reader"), Role("reader")];

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, ["Reader"]));

        // Assert
        Assert.Equal("Role 'reader' is defined more than once.", exception.Message);
    }

    [Fact]
    public void WhenARoleHasNoName_ThenResolveThrows()
    {
        // Arrange
        AuthorizationRole[] roles = [new AuthorizationRole { Name = " " }];

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, []));
        Assert.Equal("A role has no name.", exception.Message);
    }

    [Fact]
    public void WhenARoleIsNull_ThenResolveThrows()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Reader"), null!];

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, []));
        Assert.Equal("A role is null.", exception.Message);
    }

    [Fact]
    public void WhenAnIncludedNameIsEmpty_ThenResolveThrowsNamingTheIncludingRole()
    {
        // Arrange
        AuthorizationRole[] roles = [new AuthorizationRole { Name = "Agent", Includes = [null!] }];

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, ["Agent"]));

        // Assert
        Assert.Equal("Role 'Agent' includes a role with no name.", exception.Message);
    }

    [Fact]
    public void WhenARoleIsNamedTwiceAndAlsoIncluded_ThenItIsResolvedOnce()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Reader"), Role("Agent", "reader")];

        // Act
        var closure = RoleClosure.Resolve(roles, ["READER", "agent", "Reader"]);

        // Assert
        Assert.Equal(["Reader", "Agent"], closure.Select(role => role.Name));
    }

    [Fact]
    public void WhenACycleDoesNotStartAtTheNamedRole_ThenThePathStartsAtTheRepeatedRole()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Top", "A"), Role("A", "B"), Role("B", "A")];

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, ["Top"]));

        // Assert
        Assert.Equal("Role 'A' includes itself: A > B > A.", exception.Message);
    }

    [Fact]
    public void WhenRolesAreNestedTooDeep_ThenResolveThrows()
    {
        // Arrange
        var roles = Enumerable.Range(0, 70).Select(i => Role($"Role{i}", $"Role{i + 1}")).Append(Role("Role70")).ToArray();

        // Act
        var exception = Assert.Throws<AuthorizationException>(() => RoleClosure.Resolve(roles, ["Role0"]));

        // Assert
        Assert.Equal("Role 'Role64' is nested more than 64 levels deep.", exception.Message);
    }
}
