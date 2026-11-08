using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Storage.Abstractions.Metadata;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class PolicyUserPermissionsTests
{
    private static readonly AuthorizationRole UserManagerRole = new()
    {
        Name = "usermanager",
        Allow =
        [
            new PermissionRule { Paths = ["/Authorization/**"], Actions = [AuthorizationActions.Administer] },
            new PermissionRule { Paths = ["/Storage/**"], Actions = [AuthorizationActions.Administer] }
        ]
    };

    // Reads everything and administers the user "kid" only.
    private static readonly AuthorizationRole KidManagerRole = new()
    {
        Name = "kidmanager",
        Includes = [BuiltInRoles.ReaderName],
        Allow = [new PermissionRule { Paths = ["/Authorization/Users[kid]/*"], Actions = [AuthorizationActions.Administer] }]
    };

    [Fact]
    public async Task WhenTheUserHoldsReader_ThenTheyReadButDoNotWriteState()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;

        // Act & Assert
        Assert.True(graph.Permissions.CanRead(temperature));
        Assert.False(graph.Permissions.CanWrite(temperature));
    }

    [Fact]
    public async Task WhenThePersonMustChangeTheirPassword_ThenTheyMayNeitherSeeNorDoAnythingUntilTheyChangeIt()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        var administration = PermissionServiceCollectionExtensions.CreateUserAdministration(graph.UserStore, graph.CurrentUser);
        Assert.True(graph.Permissions.CanRead(temperature));

        // Act
        await graph.SignInTestUserAsync(mustChangePassword: true);
        await AsyncTestHelpers.WaitUntilAsync(() => !graph.Permissions.CanRead(temperature));
        var added = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Assert
        Assert.False(graph.Permissions.CanWrite(temperature));
        Assert.False(graph.Permissions.CanShow(graph.Device.TryGetRegisteredSubject()!));
        Assert.False(graph.Permissions.HoldsAllRoles([BuiltInRoles.ReaderName]));
        Assert.False(added.Succeeded);
        Assert.Null(graph.UserStore.GetUserId("kid"));
    }

    [Fact]
    public async Task WhenTheUserHoldsARoleThatIncludesAnother_ThenTheyHoldBothButNotTheOneAbove()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);

        // Act & Assert
        Assert.True(graph.Permissions.HoldsAllRoles([BuiltInRoles.OperatorName, BuiltInRoles.ReaderName.ToLowerInvariant()]));
        Assert.False(graph.Permissions.HoldsAllRoles([BuiltInRoles.ReaderName, "somecustomrole"]));
        Assert.False(graph.Permissions.HoldsAllRoles([BuiltInRoles.EverythingName]));
    }

    [Fact]
    public async Task WhenTheUserHoldsEverything_ThenTheyHoldAnyRoleButNotAnEmptySet()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);

        // Act & Assert
        Assert.True(graph.Permissions.HoldsAllRoles(["somecustomrole", "other"]));
        Assert.False(graph.Permissions.HoldsAllRoles([]));
    }

    [Fact]
    public async Task WhenNoUserIsSignedIn_ThenTheyHoldTheRolesOfAnonymous()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);

        // Act & Assert
        Assert.True(graph.Permissions.HoldsAllRoles([BuiltInRoles.ReaderName]));
        Assert.False(graph.Permissions.HoldsAllRoles([BuiltInRoles.OperatorName]));
    }

    [Fact]
    public async Task WhenTheUsersRolesChange_ThenTheRoleCheckFollows()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        using var changed = new ManualResetEventSlim();
        graph.Permissions.Changed += changed.Set;
        var before = graph.Permissions.HoldsAllRoles([BuiltInRoles.OperatorName]);

        // Act
        await graph.SetRolesAsync([BuiltInRoles.OperatorName]);

        // Assert
        Assert.False(before);
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(graph.Permissions.HoldsAllRoles([BuiltInRoles.OperatorName]));
    }

    [Fact]
    public async Task WhenTheUserHoldsEverything_ThenTheyAdministerAnyMemberAndInvokeAnyOperationThatRequiresAdministration()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);
        var create = graph.Storage.TryGetRegisteredSubject()!.TryGetProperty("Create")!;
        var roles = graph.Authorization.TryGetRegisteredSubject()!.TryGetProperty("Roles")!;

        // Act & Assert
        Assert.True(graph.Permissions.CanAdminister(roles));
        Assert.True(graph.Permissions.CanAdminister(create));
        Assert.True(graph.Permissions.CanInvoke(create));
    }

    [Fact]
    public async Task WhenTheUserManagesUsersOnly_ThenTheyAdministerUsersButNotOtherAdministration()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [UserManagerRole.Name], customRoles: [UserManagerRole]);
        var userRoles = (await graph.AddUserAsync("kid")).TryGetRegisteredSubject()!.TryGetProperty("Roles")!;
        var addUser = graph.Authorization.TryGetRegisteredSubject()!.TryGetProperty("AddUser")!;
        var create = graph.Storage.TryGetRegisteredSubject()!.TryGetProperty("Create")!;

        // Act & Assert
        Assert.False(graph.Permissions.CanAdminister(graph.Authorization.TryGetRegisteredSubject()!.TryGetProperty("Roles")!));
        Assert.True(graph.Permissions.CanAdminister(userRoles));
        Assert.True(graph.Permissions.CanInvoke(addUser));
        // The policy grants administer on the storage; only the scope of administration refuses it.
        Assert.True(graph.AuthorizationService.GetPolicy([UserManagerRole.Name], PrincipalKind.Person).Policy.IsAllowed(create, AuthorizationActions.Administer));
        Assert.False(graph.Permissions.CanInvoke(create));
        Assert.False(graph.Permissions.CanAdminister(create));
    }

    [Fact]
    public async Task WhenTheUserHoldsEverything_ThenTheySeeUsersAndReadButDoNotWriteTheirMembers()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);
        var admin = graph.Authorization.Users[AdminSeeding.AdminUserName].TryGetRegisteredSubject()!;
        var userName = admin.TryGetProperty(nameof(User.UserName))!;

        // Act & Assert
        Assert.True(graph.Permissions.CanShow(admin));
        Assert.True(graph.Permissions.CanRead(userName));
        Assert.True(graph.Permissions.CanReadHistory(userName));
        Assert.False(graph.Permissions.CanWrite(userName));
    }

    [Fact]
    public async Task WhenTheUserManagesOneUser_ThenTheySeeThatUserButNoOther()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [KidManagerRole.Name], customRoles: [KidManagerRole]);
        await graph.AddUserAsync("kid");
        await graph.AddUserAsync("other");
        var kid = graph.Authorization.Users["kid"].TryGetRegisteredSubject()!;
        var other = graph.Authorization.Users["other"].TryGetRegisteredSubject()!;

        // Act & Assert
        Assert.True(graph.Permissions.CanShow(kid));
        Assert.True(graph.Permissions.CanRead(kid.TryGetProperty(nameof(User.Roles))!));
        Assert.False(graph.Permissions.CanWrite(kid.TryGetProperty(nameof(User.Roles))!));
        Assert.False(graph.Permissions.CanShow(other));
        Assert.False(graph.Permissions.CanRead(other.TryGetProperty(nameof(User.Roles))!));
    }

    [Fact]
    public async Task WhenTheUserManagesUsersButCannotReadTheAuthorization_ThenUsersStayHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [UserManagerRole.Name], customRoles: [UserManagerRole]);
        var kid = (await graph.AddUserAsync("kid")).TryGetRegisteredSubject()!;

        // Act & Assert
        Assert.True(graph.Permissions.CanRead(kid.TryGetProperty(nameof(User.Roles))!));
        Assert.False(graph.Permissions.CanShow(kid));
    }

    [Fact]
    public async Task WhenTheUserHoldsOperator_ThenUsersStayHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);
        var admin = graph.Authorization.Users[AdminSeeding.AdminUserName].TryGetRegisteredSubject()!;
        var userName = admin.TryGetProperty(nameof(User.UserName))!;

        // Act & Assert
        Assert.True(graph.Permissions.CanShow(graph.Authorization.TryGetRegisteredSubject()!));
        Assert.False(graph.Permissions.CanShow(admin));
        Assert.False(graph.Permissions.CanRead(userName));
        Assert.False(graph.Permissions.CanReadHistory(userName));
    }

    [Fact]
    public async Task WhenNoUserIsSignedInAndAnonymousHoldsEverything_ThenUsersStayHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null, anonymousRoles: [BuiltInRoles.EverythingName]);
        var admin = graph.Authorization.Users[AdminSeeding.AdminUserName].TryGetRegisteredSubject()!;
        var userName = admin.TryGetProperty(nameof(User.UserName))!;

        // Act & Assert
        Assert.True(graph.Permissions.CanShow(graph.Authorization.TryGetRegisteredSubject()!));
        Assert.False(graph.Permissions.CanShow(admin));
        Assert.False(graph.Permissions.CanRead(userName));
        Assert.False(graph.Permissions.CanReadHistory(userName));
    }

    [Fact]
    public async Task WhenAnAgentHoldsEverything_ThenItNeitherSeesUsersNorReadsTheirMembers()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null);
        var admin = graph.Authorization.Users[AdminSeeding.AdminUserName].TryGetRegisteredSubject()!;
        var userName = admin.TryGetProperty(nameof(User.UserName))!;

        // Act
        var policy = graph.AuthorizationService.GetPolicy([BuiltInRoles.EverythingName]).Policy;
        var decisions = PermissionDecisions.Create(graph.AuthorizationService, [BuiltInRoles.EverythingName], PrincipalKind.NonHuman);

        // Assert
        Assert.False(policy.CanRead(userName));
        Assert.False(policy.IsVisible(admin));
        Assert.False(decisions.CanRead(userName));
        Assert.False(decisions.CanReadHistory(userName));
        Assert.False(decisions.CanShow(admin));
    }

    [Fact]
    public async Task WhenNoUserIsSignedInAndAnonymousHasNoRoles_ThenNothingIsShown()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null);

        // Act & Assert
        Assert.False(graph.Permissions.CanShow(graph.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public async Task WhenNoUserIsSignedInAndAnonymousHoldsReader_ThenTheVisitorReadsWithTheRolesOfAnonymous()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;

        // Act & Assert
        Assert.True(graph.Permissions.CanShow(graph.Device.TryGetRegisteredSubject()!));
        Assert.True(graph.Permissions.CanRead(temperature));
        Assert.False(graph.Permissions.CanWrite(temperature));
        Assert.False(graph.Permissions.CanAdminister(temperature));
    }

    [Fact]
    public async Task WhenTheSignedInUserIsRemoved_ThenNothingIsAllowedEvenThoughAnonymousHoldsRoles()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName], anonymousRoles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        using var changed = new ManualResetEventSlim();
        graph.Permissions.Changed += changed.Set;

        // Act
        await graph.RemoveTestUserAsync();

        // Assert
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(graph.Permissions.CanRead(temperature));
        Assert.False(graph.Permissions.CanShow(graph.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public async Task WhenTheUsersRolesChange_ThenChangedIsRaisedAndTheNewPolicyApplies()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        using var changed = new ManualResetEventSlim();
        graph.Permissions.Changed += changed.Set;

        // Act
        await graph.SetRolesAsync([BuiltInRoles.OperatorName]);

        // Assert
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(graph.Permissions.CanWrite(temperature));
    }

    [Fact]
    public async Task WhenTheUserSignsOut_ThenChangedIsRaisedAndTheRolesOfAnonymousApply()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        using var changed = new ManualResetEventSlim();
        graph.Permissions.Changed += changed.Set;

        // Act
        graph.SignOut();

        // Assert
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(graph.Permissions.CanRead(temperature));
    }

    [Fact]
    public async Task WhenARoleOfTheUserIsRenamed_ThenTheUserKeepsItsPolicyUnderTheNewName()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [UserManagerRole.Name], customRoles: [UserManagerRole]);
        await graph.AddUserAsync("kid");

        // Act
        var renamed = await graph.RoleStore.RenameRoleAsync(UserManagerRole.Name, "managers", CancellationToken.None);

        // Assert
        Assert.True(renamed.Succeeded, renamed.Error);
        Assert.Equal(["managers"], graph.CurrentUser.Roles);
        // The users are projected anew with the new role name, so the member is read after the rename.
        var userRoles = graph.Authorization.Users["kid"].TryGetRegisteredSubject()!.TryGetProperty("Roles")!;
        await AsyncTestHelpers.WaitUntilAsync(() => graph.Permissions.CanAdminister(userRoles));
    }

    [Fact]
    public async Task WhenTheUserHoldsOperator_ThenTheyInvokeAPlainOperationButNotOneThatRequiresAdministration()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);
        var storage = graph.Storage.TryGetRegisteredSubject()!;
        var refresh = storage.TryGetProperty("Refresh")!;
        var create = storage.TryGetProperty("Create")!;

        // Act & Assert
        Assert.True(graph.Permissions.CanInvoke(refresh));
        Assert.False(graph.Permissions.CanInvoke(create));
    }

    [Fact]
    public async Task WhenAChangedHandlerOfThePermissionsThrows_ThenLaterHandlersRunAndTheRoleChangeSucceeds()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        using var changed = new ManualResetEventSlim();
        graph.Permissions.Changed += () => throw new InvalidOperationException("A component failed.");
        graph.Permissions.Changed += changed.Set;

        // Act
        await graph.SetRolesAsync([BuiltInRoles.OperatorName]);

        // Assert
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(graph.Permissions.CanWrite(temperature));
    }

    [Fact]
    public async Task WhenAChangedHandlerOfTheCurrentUserThrows_ThenPermissionsSubscribedLaterStillFollows()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        var temperature = graph.Device.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;
        graph.CurrentUser.Changed += () => throw new InvalidOperationException("A subscriber failed.");
        using var permissions = new PolicyUserPermissions(graph.AuthorizationService, graph.CurrentUser, NullLogger<PolicyUserPermissions>.Instance);
        using var changed = new ManualResetEventSlim();
        permissions.Changed += changed.Set;

        // Act
        await graph.SetRolesAsync([BuiltInRoles.OperatorName]);

        // Assert
        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(permissions.CanWrite(temperature));
    }

    [Fact]
    public async Task WhenTheCircuitIsSignedOut_ThenItsAdministrationResolvedBeforeRefusesEveryChange()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var authenticationState = await SignInAsync(scope.ServiceProvider, AdminSeeding.AdminUserName);
        var administration = scope.ServiceProvider.GetRequiredService<IUserAdministration>();
        var beforeSignOut = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Act
        authenticationState.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        var afterSignOut = await administration.AddUserAsync("other", null, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Assert
        Assert.True(beforeSignOut.Succeeded, beforeSignOut.Error);
        Assert.False(afterSignOut.Succeeded);
        Assert.Null(host.UserStore.GetUserId("other"));
    }

    [Fact]
    public async Task WhenRevalidationSignsTheCircuitOut_ThenItsAdministrationResolvedBeforeRefusesEveryChange()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var addedBoss = await host.AdministrationAsAdmin.AddUserAsync("boss", null, [BuiltInRoles.EverythingName], CancellationToken.None);
        Assert.True(addedBoss.Succeeded, addedBoss.Error);
        await using var scope = host.Services.CreateAsyncScope();
        var authenticationState = await SignInAsync(scope.ServiceProvider, "boss");
        var administration = scope.ServiceProvider.GetRequiredService<IUserAdministration>();
        using var signedOut = new ManualResetEventSlim();
        ((AuthenticationStateProvider)authenticationState).AuthenticationStateChanged += _ => signedOut.Set();

        // Act
        var reset = await host.AdministrationAsAdmin.ResetPasswordAsync("boss", CancellationToken.None);
        Assert.True(signedOut.Wait(TimeSpan.FromSeconds(5)));
        var result = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Assert
        Assert.True(reset.Succeeded, reset.Error);
        Assert.False(result.Succeeded);
        Assert.Null(host.UserStore.GetUserId("kid"));
    }

    [Fact]
    public async Task WhenTheApplicationIsComposed_ThenEachScopeGetsThePolicyOfItsSignedInPerson()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var authenticationState = await SignInAsync(scope.ServiceProvider, AdminSeeding.AdminUserName);

        // Act
        var permissions = scope.ServiceProvider.GetRequiredService<IUserPermissions>();
        var currentUser = scope.ServiceProvider.GetRequiredService<CurrentUser>();
        var added = await scope.ServiceProvider.GetRequiredService<IUserAdministration>().AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Assert
        Assert.True(added.Succeeded, added.Error);
        Assert.IsType<HomeBlazeRevalidatingAuthenticationStateProvider>(authenticationState);
        Assert.IsType<PolicyUserPermissions>(permissions);
        Assert.True(permissions.CanAdminister(host.AuthorizationSubjects.Single().TryGetRegisteredSubject()!.TryGetProperty("Roles")!));
        Assert.Equal(host.UserStore.GetUserId(AdminSeeding.AdminUserName), currentUser.UserId);
        Assert.Equal(PrincipalKind.Person, currentUser.Kind);
        Assert.Contains(BuiltInRoles.EverythingName, currentUser.Roles);
    }

    // Sets the authentication state of the circuit scope to the principal of the user named userName, as the circuit does.
    private static async Task<IHostEnvironmentAuthenticationStateProvider> SignInAsync(IServiceProvider services, string userName)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByNameAsync(userName))!;
        // As after setting a password, without which the person may do nothing.
        user.MustChangePassword = false;
        user.AllowsEmptyPassword = false;
        Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        await services.GetRequiredService<UserStore>().RefreshUserAsync(user.Id, CancellationToken.None);
        var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
        var authenticationState = (IHostEnvironmentAuthenticationStateProvider)services.GetRequiredService<AuthenticationStateProvider>();
        authenticationState.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        return authenticationState;
    }

    [Fact]
    public async Task WhenTheOrderOfAFolderNamesAChildThePersonIsNotShown_ThenTheValueTheyAreShownLeavesItOut()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "nosecret",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Folder/Secret/**"], Actions = [PermissionAction.Read] }]
        };
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [role.Name], customRoles: [role]);
        var secret = new PermissionsTestDevice();
        var folder = new PermissionsTestFolder
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Docs"] = new PermissionsTestDevice(), ["Secret"] = secret }
        };
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Folder"] = folder };
        var values = ImmutableDictionary<string, JsonElement>.Empty
            .Add("navTitle", JsonSerializer.SerializeToElement("Folder"))
            .Add(MetadataKeys.Order, JsonSerializer.SerializeToElement(new[] { "Secret", "Docs" }));
        var registeredFolder = folder.TryGetRegisteredSubject()!;
        registeredFolder.AddProperty(KnownAttributes.Metadata, typeof(ImmutableDictionary<string, JsonElement>), _ => values, null);
        var metadata = registeredFolder.TryGetProperty(KnownAttributes.Metadata)!;

        // Act
        var shown = (ImmutableDictionary<string, JsonElement>)((IUserPermissions)graph.Permissions).GetValue(metadata)!;

        // Assert
        Assert.False(graph.Permissions.CanShow(secret.TryGetRegisteredSubject()!));
        Assert.Equal("Folder", shown["navTitle"].GetString());
        Assert.Equal(["Docs"], shown[MetadataKeys.Order].EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(2, values[MetadataKeys.Order].GetArrayLength());
    }

    [Fact]
    public async Task WhenTheChildOrderOfAFolderNamesAChildThePersonIsNotShown_ThenTheValueTheyAreShownLeavesItOut()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "nosecret",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Folder/Secret/**"], Actions = [PermissionAction.Read] }]
        };
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [role.Name], customRoles: [role]);
        var folder = new PermissionsTestFolder
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Docs"] = new PermissionsTestDevice(), ["Secret"] = new PermissionsTestDevice() },
            ChildOrder = ["Secret", "Docs"]
        };
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Folder"] = folder };
        var childOrder = folder.TryGetRegisteredSubject()!.TryGetProperty(nameof(PermissionsTestFolder.ChildOrder))!;

        // Act
        var shown = (ImmutableArray<string>)((IUserPermissions)graph.Permissions).GetValue(childOrder)!;

        // Assert
        Assert.Equal<string>(["Docs"], shown);
        Assert.Equal<string>(["Secret", "Docs"], folder.ChildOrder);
    }

    [Fact]
    public async Task WhenTheOrderOfAFolderNamesAChildShownElsewhereThroughAFolderMemberThePersonMayNotRead_ThenTheValueTheyAreShownLeavesItOut()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "nofolderchildren",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Types = [typeof(PermissionsTestFolder).FullName!], Members = [nameof(PermissionsTestFolder.Children)], Actions = [PermissionAction.Read] }]
        };
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [role.Name], customRoles: [role]);
        var docs = new PermissionsTestDevice();
        var folder = new PermissionsTestFolder { Children = new Dictionary<string, IInterceptorSubject> { ["Docs"] = docs } };
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Folder"] = folder, ["Alias"] = docs };
        var values = ImmutableDictionary<string, JsonElement>.Empty
            .Add(MetadataKeys.Order, JsonSerializer.SerializeToElement(new[] { "Docs" }));
        var registeredFolder = folder.TryGetRegisteredSubject()!;
        registeredFolder.AddProperty(KnownAttributes.Metadata, typeof(ImmutableDictionary<string, JsonElement>), _ => values, null);
        var metadata = registeredFolder.TryGetProperty(KnownAttributes.Metadata)!;

        // Act
        var shown = (ImmutableDictionary<string, JsonElement>)((IUserPermissions)graph.Permissions).GetValue(metadata)!;

        // Assert
        Assert.True(graph.Permissions.CanShow(docs.TryGetRegisteredSubject()!));
        Assert.False(graph.Permissions.CanRead(registeredFolder.TryGetProperty(nameof(PermissionsTestFolder.Children))!));
        Assert.Empty(shown[MetadataKeys.Order].EnumerateArray());
    }
}
