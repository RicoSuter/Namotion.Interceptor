namespace Namotion.Interceptor.Authorization.Tests;

public class RecordEqualityTests
{
    private static PermissionRule CreateRule() => new()
    {
        Paths = ["/Rooms[Kitchen]", "/Wifi"],
        Types = ["Light"],
        Members = ["Bright*"],
        Tags = ["Secret"],
        Actions = [PermissionAction.Read, PermissionAction.Write]
    };

    private static AuthorizationRole CreateRole(string name = "Reader") => new()
    {
        Name = name,
        Includes = ["Viewer"],
        Allow = [CreateRule()],
        Deny = [CreateRule() with { Actions = [PermissionAction.Write] }]
    };

    [Fact]
    public void WhenRulesAreBuiltFromTheSameLiterals_ThenTheyAreEqualWithTheSameHashCode()
    {
        // Arrange
        var first = CreateRule();
        var second = CreateRule();

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.True(equal);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void WhenActionNamesDifferOnlyInCase_ThenRulesAreEqual()
    {
        // Arrange
        var first = new PermissionRule { Actions = [new PermissionAction("execute")] };
        var second = new PermissionRule { Actions = [new PermissionAction("EXECUTE")] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.True(equal);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void WhenTagsDifferOnlyInCase_ThenRulesAreEqual()
    {
        // Arrange
        var first = new PermissionRule { Tags = ["secret"], Actions = [PermissionAction.Read] };
        var second = new PermissionRule { Tags = ["SECRET"], Actions = [PermissionAction.Read] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.True(equal);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void WhenASelectorValueDiffers_ThenRulesAreNotEqual()
    {
        // Arrange
        var first = CreateRule();
        var second = CreateRule() with { Members = ["Brightness"] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.False(equal);
    }

    [Fact]
    public void WhenTheOrderOfValuesDiffers_ThenRulesAreNotEqual()
    {
        // Arrange
        var first = CreateRule();
        var second = CreateRule() with { Paths = ["/Wifi", "/Rooms[Kitchen]"] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.False(equal);
    }

    [Fact]
    public void WhenAnActionDiffers_ThenRulesAreNotEqual()
    {
        // Arrange
        var first = CreateRule();
        var second = CreateRule() with { Actions = [PermissionAction.Read] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.False(equal);
    }

    [Fact]
    public void WhenRoleNamesDifferOnlyInCase_ThenRolesAreEqualWithTheSameHashCode()
    {
        // Arrange
        var first = CreateRole("Reader");
        var second = CreateRole("READER");

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.True(equal);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void WhenAnIncludeDiffers_ThenRolesAreNotEqual()
    {
        // Arrange
        var first = CreateRole();
        var second = CreateRole() with { Includes = ["Operator"] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.False(equal);
    }

    [Fact]
    public void WhenARuleDiffers_ThenRolesAreNotEqual()
    {
        // Arrange
        var first = CreateRole();
        var second = CreateRole() with { Allow = [CreateRule() with { Tags = ["Public"] }] };

        // Act
        var equal = first.Equals(second);

        // Assert
        Assert.False(equal);
    }

    [Fact]
    public void WhenRecordsAreCopiedWithNoChanges_ThenTheCopiesAreEqual()
    {
        // Arrange
        var rule = CreateRule();
        var role = CreateRole();

        // Act
        var ruleCopy = rule with { };
        var roleCopy = role with { };

        // Assert
        Assert.Equal(rule, ruleCopy);
        Assert.Equal(rule.GetHashCode(), ruleCopy.GetHashCode());
        Assert.Equal(role, roleCopy);
        Assert.Equal(role.GetHashCode(), roleCopy.GetHashCode());
    }
}
