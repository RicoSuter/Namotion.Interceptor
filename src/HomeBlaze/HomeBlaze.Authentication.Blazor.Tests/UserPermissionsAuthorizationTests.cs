using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class UserPermissionsAuthorizationTests
{
    private const string Anonymous = "(signed out, anonymous holds reader)";

    private static readonly AuthorizationRole UserManagerRole = new()
    {
        Name = "usermanager",
        Allow =
        [
            new PermissionRule { Paths = ["/Authorization/**"], Actions = [AuthorizationActions.Administer, AuthorizationActions.Execute] },
            new PermissionRule { Paths = ["/Storage/**"], Actions = [AuthorizationActions.Administer] }
        ]
    };

    [Fact]
    public async Task WhenThePersonIsAFullAdministrator_ThenTheFullAdministratorPolicySucceeds()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);

        // Act
        var succeeded = await graph.AuthorizeAsync(HomeBlazePolicies.FullAdministrator);

        // Assert
        Assert.True(succeeded);
    }

    [Fact]
    public async Task WhenThePersonHoldsOperator_ThenTheFullAdministratorPolicyFails()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);

        // Act
        var succeeded = await graph.AuthorizeAsync(HomeBlazePolicies.FullAdministrator);

        // Assert
        Assert.False(succeeded);
    }

    [Fact]
    public async Task WhenNobodyIsSignedInAndAnonymousHoldsEverything_ThenTheFullAdministratorPolicyFails()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null, anonymousRoles: [BuiltInRoles.EverythingName]);

        // Act
        var succeeded = await graph.AuthorizeAsync(HomeBlazePolicies.FullAdministrator);

        // Assert
        Assert.False(succeeded);
    }

    [Fact]
    public async Task WhenTheFullAdministratorIsRemoved_ThenTheFullAdministratorPolicyFails()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);
        await graph.RemoveTestUserAsync();

        // Act
        var succeeded = await graph.AuthorizeAsync(HomeBlazePolicies.FullAdministrator);

        // Assert
        Assert.False(succeeded);
    }

    public static TheoryData<string> RoleSets => new() { BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, UserManagerRole.Name, Anonymous };

    [Theory]
    [MemberData(nameof(RoleSets))]
    public async Task WhenAMemberActionIsAuthorized_ThenTheResultAgreesWithTheUserPermissions(string roleName)
    {
        // Arrange
        await using var graph = roleName == Anonymous
            ? await PermissionsTestGraph.CreateAsync(roles: null, customRoles: [UserManagerRole], anonymousRoles: [BuiltInRoles.ReaderName])
            : await PermissionsTestGraph.CreateAsync(roles: [roleName], customRoles: [UserManagerRole]);
        var kid = (await graph.AddUserAsync("kid")).TryGetRegisteredSubject()!;
        var storage = graph.Storage.TryGetRegisteredSubject()!;
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var cases = new (string Name, RegisteredSubjectProperty Member, MemberActionRequirement Requirement, bool Expected)[]
        {
            ("read temperature", temperature, MemberActionRequirement.Read, graph.Permissions.CanRead(temperature)),
            ("write temperature", temperature, MemberActionRequirement.Write, graph.Permissions.CanWrite(temperature)),
            ("history of temperature", temperature, MemberActionRequirement.History, graph.Permissions.CanReadHistory(temperature)),
            ("execute refresh", storage.TryGetProperty("Refresh")!, MemberActionRequirement.Execute, graph.Permissions.CanInvoke(storage.TryGetProperty("Refresh")!)),
            ("execute create", storage.TryGetProperty("Create")!, MemberActionRequirement.Execute, graph.Permissions.CanInvoke(storage.TryGetProperty("Create")!)),
            ("administer create", storage.TryGetProperty("Create")!, MemberActionRequirement.Administer, graph.Permissions.CanAdminister(storage.TryGetProperty("Create")!)),
            ("execute remove of a user", kid.TryGetProperty("Remove")!, MemberActionRequirement.Execute, graph.Permissions.CanInvoke(kid.TryGetProperty("Remove")!)),
            ("administer roles of a user", kid.TryGetProperty("Roles")!, MemberActionRequirement.Administer, graph.Permissions.CanAdminister(kid.TryGetProperty("Roles")!))
        };

        // Act
        var actual = new List<string>();
        foreach (var (name, member, requirement, _) in cases)
        {
            actual.Add($"{name}: {await graph.AuthorizeAsync(member, requirement)}");
        }

        // Assert
        Assert.Equal(cases.Select(item => $"{item.Name}: {item.Expected}"), actual);
    }

    [Fact]
    public async Task WhenTheUserManagesUsersOnly_ThenTheMemberActionsMatchTheScopeOfAdministration()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [UserManagerRole.Name], customRoles: [UserManagerRole]);
        var kid = (await graph.AddUserAsync("kid")).TryGetRegisteredSubject()!;
        var create = graph.Storage.TryGetRegisteredSubject()!.TryGetProperty("Create")!;
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;

        // Act & Assert
        Assert.True(await graph.AuthorizeAsync(kid.TryGetProperty("Roles")!, MemberActionRequirement.Administer));
        Assert.True(await graph.AuthorizeAsync(kid.TryGetProperty("Remove")!, MemberActionRequirement.Execute));
        Assert.False(await graph.AuthorizeAsync(create, MemberActionRequirement.Execute));
        Assert.False(await graph.AuthorizeAsync(temperature, MemberActionRequirement.Read));
    }

    [Fact]
    public async Task WhenTheResourceIsNoMember_ThenTheMemberActionFails()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);

        // Act
        var succeeded = await graph.AuthorizeAsync(graph.Device, MemberActionRequirement.Read);

        // Assert
        Assert.False(succeeded);
    }
}
