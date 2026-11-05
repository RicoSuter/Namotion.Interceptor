using System.Linq;
using static Namotion.Interceptor.Authorization.Tests.Policies;

namespace Namotion.Interceptor.Authorization.Tests;

public class SelectorValidationTests
{
    private static AuthorizationRole[] Chain(int roleCount)
        => Enumerable.Range(0, roleCount)
            .Select(i => Role($"Role{i}", includes: i < roleCount - 1 ? [$"Role{i + 1}"] : null))
            .ToArray();

    [Fact]
    public void WhenIncludesAreNestedExactly64LevelsDeep_ThenThePolicyCompiles()
    {
        // Arrange
        var roles = Chain(65);

        // Act
        var policy = Compile(roles, ["Role0"], systemDeny: []);

        // Assert
        Assert.Equal(65, policy.Roles.Length);
    }

    [Fact]
    public void WhenIncludesAreNested65LevelsDeep_ThenCompileThrows()
    {
        // Arrange
        var roles = Chain(66);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(roles, ["Role0"], systemDeny: []));
        Assert.Contains("Role65", exception.Message);
    }

    [Theory]
    [InlineData("Password ")]
    [InlineData(" Password")]
    [InlineData("Pass​word")]
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
    [InlineData("Li​ght")]
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
    [InlineData("sec​ret")]
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
    [InlineData("Ad​min")]
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
    [InlineData("Ba​se")]
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
        var role = Role("Reader", deny: [Rule([PermissionAction.Read], paths: ["/​Wifi/**"])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Compile(role));
        Assert.Contains("'Reader'", exception.Message);
        Assert.Contains("invisible", exception.Message);
    }
}
