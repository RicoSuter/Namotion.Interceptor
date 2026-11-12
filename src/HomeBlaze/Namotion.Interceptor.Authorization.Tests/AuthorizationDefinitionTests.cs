using System.Collections.Immutable;
using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class AuthorizationDefinitionTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];

    [Fact]
    public void WhenOneRoleHasAnInvalidRule_ThenCreateSucceedsAndPrincipalsNotReachingItAreUnaffected()
    {
        // Arrange
        var home = Create();
        AuthorizationRole[] roles = [Role("Reader", allow: [Rule(Read, tags: ["state"])]), Role("Broken", allow: [Rule([], tags: ["state"])])];

        // Act
        var definition = Define(roles);
        var reader = definition.Compile(["Reader"]);

        // Assert
        Assert.True(reader.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.False(reader.CanWrite(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenAPrincipalHoldsARoleWithAnInvalidRule_ThenCompileThrowsItsError()
    {
        // Arrange
        var definition = Define([Role("Reader", allow: [Rule(Read)]), Role("Broken", allow: [Rule([], tags: ["state"])])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => definition.Compile(["Reader", "Broken"]));
        Assert.Equal("A rule of role 'Broken' has no actions.", exception.Message);
    }

    [Fact]
    public void WhenAPrincipalIncludesARoleWithAnInvalidRuleTransitively_ThenCompileThrowsItsError()
    {
        // Arrange
        var definition = Define(
        [
            Role("Top", includes: ["Middle"]),
            Role("Middle", includes: ["Broken"]),
            Role("Broken", deny: [Rule(Read, paths: ["Rooms[Kitchen]"])]),
            Role("Reader", allow: [Rule(Read)])
        ]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => definition.Compile(["Top"]));
        Assert.StartsWith("Path pattern 'Rooms[Kitchen]' of role 'Broken'", exception.Message);
        Assert.Equal(["Reader"], definition.Compile(["Reader"]).Roles.Select(role => role.Name));
    }

    [Fact]
    public void WhenSeveralRolesOfAPrincipalAreBroken_ThenCompileThrowsTheErrorOfTheFirstInClosureOrder()
    {
        // Arrange
        var definition = Define(
        [
            Role("Agent", allow: [Rule([new PermissionAction("histroy")])], includes: ["Base"]),
            Role("Base", allow: [Rule([], tags: ["state"])])
        ]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => definition.Compile(["Agent"]));
        Assert.Equal("A rule of role 'Base' has no actions.", exception.Message);
    }

    [Fact]
    public void WhenARoleIncludesAnUnknownRole_ThenOnlyPrincipalsReachingItCannotCompile()
    {
        // Arrange
        var definition = Define([Role("Reader", allow: [Rule(Read)]), Role("Agent", includes: ["Missing"]), Role("Top", includes: ["Agent"])]);

        // Act
        var reader = definition.Compile(["Reader"]);
        var agentException = Assert.Throws<AuthorizationException>(() => definition.Compile(["Agent"]));
        var topException = Assert.Throws<AuthorizationException>(() => definition.Compile(["Top"]));

        // Assert
        Assert.Equal(["Reader"], reader.Roles.Select(role => role.Name));
        Assert.Equal("Role 'Agent' includes 'Missing', which is not defined.", agentException.Message);
        Assert.Equal("Role 'Agent' includes 'Missing', which is not defined.", topException.Message);
    }

    [Fact]
    public void WhenRolesFormACycle_ThenExactlyThePrincipalsReachingItCannotCompile()
    {
        // Arrange
        var definition = Define(
        [
            Role("Reader", allow: [Rule(Read)]),
            Role("A", includes: ["B"]),
            Role("B", includes: ["A"]),
            Role("Entry", includes: ["Reader", "B"])
        ]);

        // Act
        var reader = definition.Compile(["Reader"]);
        var fromA = Assert.Throws<AuthorizationException>(() => definition.Compile(["A"]));
        var fromB = Assert.Throws<AuthorizationException>(() => definition.Compile(["B"]));
        var fromEntry = Assert.Throws<AuthorizationException>(() => definition.Compile(["Entry"]));

        // Assert
        Assert.Equal(["Reader"], reader.Roles.Select(role => role.Name));
        Assert.Equal("Role 'A' includes itself: A > B > A.", fromA.Message);
        Assert.Equal("Role 'B' includes itself: B > A > B.", fromB.Message);
        Assert.Equal("Role 'B' includes itself: B > A > B.", fromEntry.Message);
    }

    [Fact]
    public void WhenAChainIsNestedTooDeep_ThenOnlyPrincipalsReachingBeyondTheLimitCannotCompile()
    {
        // Arrange
        var chain = Enumerable.Range(1, 65)
            .Select(index => index < 65 ? Role($"Role{index}", includes: [$"Role{index + 1}"]) : Role($"Role{index}"))
            .ToArray();
        var definition = Define([.. chain, Role("Role0", includes: ["Role1"])]);

        // Act
        var fromRole1 = definition.Compile(["Role1"]);
        var fromRole0 = Assert.Throws<AuthorizationException>(() => definition.Compile(["Role0"]));

        // Assert
        Assert.Equal(65, fromRole1.Roles.Length);
        Assert.Equal("Role 'Role65' is nested more than 64 levels deep.", fromRole0.Message);
    }

    [Fact]
    public void WhenARoleIsDefinedTwice_ThenCreateThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => Define([Role("Reader"), Role("Reader")]));
        Assert.Equal("Role 'Reader' is defined more than once.", exception.Message);
    }

    [Fact]
    public void WhenASystemDenyRuleIsInvalid_ThenCreateThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(
            () => AuthorizationDefinition.Create([Role("Reader")], [Rule([], tags: ["secret"])], KnownActions, SlashPaths, TestTags.Default));
        Assert.Equal("A rule of the system rules has no actions.", exception.Message);
    }

    [Fact]
    public void WhenAPrincipalRoleNameIsUnknown_ThenCompileThrows()
    {
        // Arrange
        var definition = Define([Role("Reader", allow: [Rule(Read)])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => definition.Compile(["Writer"]));
        Assert.Equal("Role 'Writer' is not defined.", exception.Message);
    }

    [Fact]
    public void WhenAPrincipalRoleNameIsEmpty_ThenCompileThrows()
    {
        // Arrange
        var definition = Define([Role("Reader", allow: [Rule(Read)])]);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => definition.Compile([" "]));
        Assert.Equal("A role name is empty.", exception.Message);
    }

    [Fact]
    public void WhenOneDefinitionCompilesSeveralPrincipals_ThenEachPolicyHasOnlyItsOwnRoles()
    {
        // Arrange
        var home = Create();
        var ssid = Member(home.Wifi!, nameof(Wifi.Ssid));
        var definition = Define(
        [
            Role("Reader", allow: [Rule(Read, tags: ["state"])]),
            Role("Writer", allow: [Rule([PermissionAction.Write], tags: ["state"])]),
            Role("Operator", includes: ["Reader", "Writer"])
        ]);

        // Act
        var reader = definition.Compile(["Reader"]);
        var writer = definition.Compile(["Writer"]);
        var operatorPolicy = definition.Compile(["Operator"]);

        // Assert
        Assert.Equal(["Reader"], reader.Roles.Select(role => role.Name));
        Assert.True(reader.CanRead(ssid));
        Assert.False(reader.CanWrite(ssid));
        Assert.Equal(["Writer"], writer.Roles.Select(role => role.Name));
        Assert.False(writer.CanRead(ssid));
        Assert.True(writer.CanWrite(ssid));
        Assert.Equal(["Reader", "Writer", "Operator"], operatorPolicy.Roles.Select(role => role.Name));
        Assert.True(operatorPolicy.CanRead(ssid));
        Assert.True(operatorPolicy.CanWrite(ssid));
    }

    [Fact]
    public void WhenOneDefinitionCompilesSeveralPrincipals_ThenTheSystemDenyRulesApplyToEach()
    {
        // Arrange
        var home = Create();
        var password = Member(home.Wifi!, nameof(Wifi.Password));
        var definition = AuthorizationDefinition.Create(
            [Role("Reader", allow: [Rule(Read)]), Role("Admin", allow: [Rule(Read)])],
            [Rule(Read, tags: ["secret"])],
            KnownActions,
            SlashPaths,
            TestTags.Default);

        // Act
        var reader = definition.Compile(["Reader"]);
        var admin = definition.Compile(["Admin"]);

        // Assert
        Assert.False(reader.CanRead(password));
        Assert.False(admin.CanRead(password));
        Assert.True(admin.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenNoTagProviderIsGivenAndNoRuleHasTags_ThenTheRulesEvaluate()
    {
        // Arrange
        var home = Create();
        var definition = AuthorizationDefinition.Create([Role("Reader", allow: [Rule(Read, members: ["Ssid"])])], [], KnownActions, SlashPaths);

        // Act
        var policy = definition.Compile(["Reader"]);

        // Assert
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
        Assert.False(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
    }

    [Fact]
    public void WhenNoTagProviderIsGivenAndARoleRuleHasTags_ThenCompileOfThatRoleThrows()
    {
        // Arrange
        AuthorizationRole[] roles = [Role("Reader", deny: [Rule(Read, tags: ["secret"])]), Role("Writer", allow: [Rule([PermissionAction.Write])])];
        var definition = AuthorizationDefinition.Create(roles, [], KnownActions, SlashPaths);

        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(() => definition.Compile(["Reader"]));
        Assert.Equal("A rule of role 'Reader' selects tags, but no tag provider is given.", exception.Message);
        Assert.Equal(["Writer"], definition.Compile(["Writer"]).Roles.Select(role => role.Name));
    }

    [Fact]
    public void WhenNoTagProviderIsGivenAndASystemDenyRuleHasTags_ThenCreateThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<AuthorizationException>(
            () => AuthorizationDefinition.Create([], [Rule(Read, tags: ["secret"])], KnownActions, SlashPaths));
        Assert.Equal("A rule of the system rules selects tags, but no tag provider is given.", exception.Message);
    }

    [Fact]
    public void WhenTheSystemDenyRulesAreADefaultArray_ThenTheyAreEmpty()
    {
        // Arrange
        var home = Create();
        var definition = AuthorizationDefinition.Create([Role("Reader", allow: [Rule(Read)])], default(ImmutableArray<PermissionRule>), KnownActions, SlashPaths);

        // Act
        var policy = definition.Compile(["Reader"]);

        // Assert
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Password))));
    }

    private static AuthorizationDefinition Define(AuthorizationRole[] roles)
        => AuthorizationDefinition.Create(roles, [], KnownActions, SlashPaths, TestTags.Default);
}
