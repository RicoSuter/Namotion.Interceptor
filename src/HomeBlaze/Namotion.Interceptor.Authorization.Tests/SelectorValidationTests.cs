using System.Linq;
using static Namotion.Interceptor.Authorization.Tests.Policies;

namespace Namotion.Interceptor.Authorization.Tests;

public class SelectorValidationTests
{
    [Theory]
    [InlineData("Password ")]
    [InlineData(" Password")]
    [InlineData("Pass\u200Bword")]
    [InlineData("Pass\u0007word")]
    public void WhenAMemberSelectorHasOuterWhitespaceOrAnInvisibleCharacter_ThenCompileThrows(string value)
    {
        // Arrange
        var role = Role("Reader", deny: [Rule([PermissionAction.Read], members: [value])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));
        Assert.Contains("'Reader'", exception.Message);
        Assert.Contains("member", exception.Message);
    }

    [Theory]
    [InlineData("Light ")]
    [InlineData(" Light")]
    [InlineData("Li\u200Bght")]
    public void WhenATypeSelectorHasOuterWhitespaceOrAnInvisibleCharacter_ThenCompileThrows(string value)
    {
        // Arrange
        var role = Role("Reader", deny: [Rule([PermissionAction.Read], types: [value])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));
        Assert.Contains("'Reader'", exception.Message);
        Assert.Contains("type", exception.Message);
    }

    [Theory]
    [InlineData("secret ")]
    [InlineData(" secret")]
    [InlineData("sec\u200Bret")]
    public void WhenATagSelectorHasOuterWhitespaceOrAnInvisibleCharacter_ThenCompileThrows(string value)
    {
        // Arrange
        var role = Role("Reader", deny: [Rule([PermissionAction.Read], tags: [value])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));
        Assert.Contains("'Reader'", exception.Message);
        Assert.Contains("tag", exception.Message);
    }

    [Theory]
    [InlineData("System ")]
    [InlineData(" Admin")]
    [InlineData("Ad\u200Bmin")]
    public void WhenARoleNameHasOuterWhitespaceOrAnInvisibleCharacter_ThenCompileThrows(string name)
    {
        // Arrange
        var role = Role(name);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));
        Assert.Contains($"'{name}'", exception.Message);
    }

    [Theory]
    [InlineData("Base ")]
    [InlineData(" Base")]
    [InlineData("Ba\u200Bse")]
    public void WhenAnIncludeNameHasOuterWhitespaceOrAnInvisibleCharacter_ThenCompileThrows(string include)
    {
        // Arrange
        var roles = new[] { Role("Base"), Role("Agent", includes: [include]) };

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(roles, ["Agent"], systemDeny: []));
        Assert.Contains($"'{include}'", exception.Message);
        Assert.Contains("whitespace or an invisible", exception.Message);
        Assert.Contains("'Agent'", exception.Message);
    }

    [Fact]
    public void WhenAPathPatternContainsAnInvisibleCharacter_ThenCompileThrows()
    {
        // Arrange
        var role = Role("Reader", deny: [Rule([PermissionAction.Read], paths: ["/\u200BWifi/**"])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));
        Assert.Contains("'Reader'", exception.Message);
        Assert.Contains("invisible", exception.Message);
    }

    [Theory]
    [InlineData("member", "Pass*")]
    [InlineData("member", "*word")]
    [InlineData("type", "Namotion.Interceptor.Authorization.Tests.Models.*")]
    [InlineData("type", "Namotion.Interceptor.Authorization.Tests.Models.Light")]
    [InlineData("type", "Namotion.Interceptor.Authorization.Tests.Outer+Nested")]
    [InlineData("type", "Namotion.Interceptor.Authorization.Tests.Generic`1")]
    [InlineData("tag", "secret-data")]
    [InlineData("tag", "area:kitchen")]
    [InlineData("tag", "gr\u00FC\u00DFe")]
    [InlineData("member", "Pa\u00DFwort")]
    [InlineData("path", "/Rooms[Kitchen]/**")]
    [InlineData("path", "/Rooms['it''s']/*")]
    [InlineData("path", "/Rooms['']/**")]
    [InlineData("path", "/Zimmer[K\u00FCche]/Sensor")]
    public void WhenASelectorIsLegitimate_ThenCompileSucceeds(string kind, string value)
    {
        // Arrange
        var rule = kind switch
        {
            "member" => Rule([PermissionAction.Read], members: [value]),
            "type" => Rule([PermissionAction.Read], types: [value]),
            "tag" => Rule([PermissionAction.Read], tags: [value]),
            _ => Rule([PermissionAction.Read], paths: [value])
        };
        var role = Role("Reader", deny: [rule]);

        // Act
        var policy = Compile(role);

        // Assert
        Assert.Single(policy.Roles);
    }
}
