using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Xunit;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Tests;

public class UserStoreTests
{
    [Fact]
    public async Task WhenAUserIsAdded_ThenTheProjectionShowsItAndASetupCodeIsReturned()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var result = await administration.AddUserAsync("mom", "mom@example.com", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.False(string.IsNullOrEmpty(result.SetupCode));
        Assert.Equal([BuiltInRoles.OperatorName], graph.Authorization.Users["mom"].Roles);
    }

    [Fact]
    public async Task WhenTheLastFullAdministratorWouldLoseEverything_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var result = await administration.SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("last", result.Error);
        Assert.Equal("'admin' is the last unlocked full administrator; give another user everything without a role that denies administer first.", result.Error);
        Assert.Equal([BuiltInRoles.EverythingName], graph.UserStore.GetRoles(graph.AdminUserId));
    }

    [Fact]
    public async Task WhenRolesChange_ThenUserChangedIsRaisedAndTheStampIsUnchanged()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await administration.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        var momId = graph.UserStore.GetUserId("mom")!;
        var stampBefore = await graph.GetSecurityStampAsync(momId);
        var changed = new List<string>();
        graph.UserStore.UserChanged += changed.Add;

        // Act
        await administration.SetRolesAsync("mom", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.Contains(momId, changed);
        Assert.Equal(stampBefore, await graph.GetSecurityStampAsync(momId));
    }

    [Fact]
    public async Task WhenAUserChangedHandlerThrows_ThenTheChangeSucceedsAndLaterHandlersRun()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await administration.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        var momId = graph.UserStore.GetUserId("mom")!;
        var changed = new List<string>();
        graph.UserStore.UserChanged += _ => throw new InvalidOperationException("A handler failed.");
        graph.UserStore.UserChanged += changed.Add;

        // Act
        var result = await administration.SetRolesAsync("mom", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([momId], changed);
        Assert.Equal([BuiltInRoles.OperatorName], graph.UserStore.GetRoles(momId));
    }

    [Fact]
    public async Task WhenTheAnonymousUserIsRemoved_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).RemoveAsync(NameRules.AnonymousUserName, CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("The built-in user 'anonymous' cannot be removed, locked or have its password reset.", result.Error);
        Assert.NotNull(graph.UserStore.GetUserId(NameRules.AnonymousUserName));
    }

    [Fact]
    public async Task WhenAUserIsAddedWithABuiltInName_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var builtIn = await administration.AddUserAsync("mcp", null, [], CancellationToken.None);

        // Assert
        Assert.False(builtIn.Succeeded);
        Assert.Equal("The user name 'mcp' is reserved for a built-in user.", builtIn.Error);
    }

    [Theory]
    [InlineData("Rico")]
    [InlineData("MOM")]
    [InlineData("mo\u200Bm")]
    [InlineData("rico suter")]
    [InlineData(".rico")]
    public async Task WhenAUserIsAddedWithANameBreakingTheNameRule_ThenItIsRefusedAndNothingIsAdded(string userName)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var result = await administration.AddUserAsync(userName, null, [], CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(
            "The user name is not valid. Use lowercase letters a-z, digits, '.', '-' or '_', starting with a letter or digit, at most 64 characters.",
            result.Error);
        Assert.Null(graph.UserStore.GetUserId(userName.ToLowerInvariant()));
    }

    [Fact]
    public async Task WhenAUserIsRemoved_ThenTheirExternalLoginsAreDeletedWithThem()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await administration.AddUserAsync("mom", null, [], CancellationToken.None);
        await graph.AddExternalLoginAsync("mom", "TestProvider", "key-1");

        // Act
        await administration.RemoveAsync("mom", CancellationToken.None);

        // Assert
        Assert.Equal(0, await graph.CountExternalLoginsAsync("TestProvider", "key-1"));
    }

    [Fact]
    public async Task WhenNobodyIsSignedIn_ThenEveryChangeIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(null);

        // Act
        var added = await administration.AddUserAsync("mom", null, [], CancellationToken.None);
        var reset = await administration.ResetPasswordAsync("admin", CancellationToken.None);

        // Assert
        Assert.False(added.Succeeded);
        Assert.Equal("Only a signed-in person with an active account can manage users.", added.Error);
        Assert.False(reset.Succeeded);
        Assert.Equal("Only a signed-in person with an active account can manage users.", reset.Error);
        Assert.Null(graph.UserStore.GetUserId("mom"));
    }

    [Fact]
    public async Task WhenAnUnknownRoleIsAssigned_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).AddUserAsync("mom", null, ["ghost"], CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("Unknown role: ghost", result.Error);
        Assert.Null(graph.UserStore.GetUserId("mom"));
    }

    [Fact]
    public async Task WhenAPersonWithoutAdministerChangesAUser_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("dad", [BuiltInRoles.OperatorName]);
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);

        // Act
        var result = await graph.AdministrationAs(graph.UserStore.GetUserId("dad")).SetRolesAsync("mom", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("You are not allowed to manage the user 'mom'.", result.Error);
        Assert.Equal([BuiltInRoles.ReaderName], graph.UserStore.GetRoles(graph.UserStore.GetUserId("mom")!));
    }

    [Fact]
    public async Task WhenAManagerWithATypeRuleAddsUsers_ThenOnlyRolesTheyHoldAreAssigned()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, UserManagerRole(), null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager"]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));

        // Act
        var held = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        var notHeld = await administration.AddUserAsync("guest", null, [BuiltInRoles.EverythingName], CancellationToken.None);
        var admin = await administration.ResetPasswordAsync("admin", CancellationToken.None);

        // Assert
        Assert.True(held.Succeeded, held.Error);
        Assert.False(notHeld.Succeeded);
        Assert.Equal("You can assign only roles you hold. Not held: everything.", notHeld.Error);
        Assert.Null(graph.UserStore.GetUserId("guest"));
        Assert.False(admin.Succeeded);
        Assert.Equal("This user holds roles you do not hold: everything.", admin.Error);
    }

    [Theory]
    [InlineData(NameRules.AnonymousUserName)]
    [InlineData(NameRules.McpUserName)]
    public async Task WhenAFullAdministratorLocksOrResetsABuiltInUser_ThenItIsRefusedButTheirRolesCanChange(string userName)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var locked = await administration.SetLockedAsync(userName, true, CancellationToken.None);
        var reset = await administration.ResetPasswordAsync(userName, CancellationToken.None);
        var roles = await administration.SetRolesAsync(userName, [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.False(locked.Succeeded);
        Assert.Equal($"The built-in user '{userName}' cannot be removed, locked or have its password reset.", locked.Error);
        Assert.False(reset.Succeeded);
        Assert.Equal($"The built-in user '{userName}' cannot be removed, locked or have its password reset.", reset.Error);
        Assert.True(roles.Succeeded, roles.Error);
        Assert.Equal([BuiltInRoles.OperatorName], userName == NameRules.McpUserName ? graph.UserStore.McpRoles : graph.UserStore.AnonymousRoles);
    }

    [Fact]
    public async Task WhenAUserIsLockedAndUnlocked_ThenTheProjectionShowsItAndTheStampChanges()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var stampBefore = await graph.GetSecurityStampAsync(graph.UserStore.GetUserId("mom")!);

        // Act
        var locked = await administration.SetLockedAsync("mom", true, CancellationToken.None);
        var isLockedOut = graph.Authorization.Users["mom"].IsLockedOut;
        var unlocked = await administration.SetLockedAsync("mom", false, CancellationToken.None);

        // Assert
        Assert.True(locked.Succeeded, locked.Error);
        Assert.True(unlocked.Succeeded, unlocked.Error);
        Assert.True(isLockedOut);
        Assert.False(graph.Authorization.Users["mom"].IsLockedOut);
        Assert.NotEqual(stampBefore, await graph.GetSecurityStampAsync(graph.UserStore.GetUserId("mom")!));
    }

    [Fact]
    public async Task WhenAUserIsLockedAndUnlocked_ThenOnlyASessionWithTheNewStampIsAllowed()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var momId = graph.UserStore.GetUserId("mom")!;
        var stampBefore = await graph.GetSecurityStampAsync(momId);
        var administration = graph.AdministrationAs(graph.AdminUserId);
        Assert.True((await administration.SetLockedAsync("mom", true, CancellationToken.None)).Succeeded);
        Assert.True((await administration.SetLockedAsync("mom", false, CancellationToken.None)).Succeeded);

        // Act
        var isOldSessionAllowed = graph.UserStore.IsSessionAllowed(momId, stampBefore);
        var isNewSessionAllowed = graph.UserStore.IsSessionAllowed(momId, await graph.GetSecurityStampAsync(momId));
        var isSessionWithoutStampAllowed = graph.UserStore.IsSessionAllowed(momId, null);

        // Assert
        Assert.False(isOldSessionAllowed);
        Assert.True(isNewSessionAllowed);
        Assert.False(isSessionWithoutStampAllowed);
    }

    [Fact]
    public async Task WhenAUserIsLockedByAnAdministrator_ThenTheyHoldNoRolesUntilUnlocked()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await graph.AddUserAsync("mom", [BuiltInRoles.OperatorName]);
        var momId = graph.UserStore.GetUserId("mom")!;

        // Act
        var locked = await administration.SetLockedAsync("mom", true, CancellationToken.None);
        var rolesWhileLocked = graph.UserStore.GetRoles(momId);
        await graph.UserStore.LoadAsync(CancellationToken.None);
        var rolesAfterReload = graph.UserStore.GetRoles(momId);
        var unlocked = await administration.SetLockedAsync("mom", false, CancellationToken.None);

        // Assert
        Assert.True(locked.Succeeded, locked.Error);
        Assert.True(unlocked.Succeeded, unlocked.Error);
        Assert.Empty(rolesWhileLocked);
        Assert.Empty(rolesAfterReload);
        Assert.Equal([BuiltInRoles.OperatorName], graph.Authorization.Users["mom"].Roles);
        Assert.Equal([BuiltInRoles.OperatorName], graph.UserStore.GetRoles(momId));
    }

    [Fact]
    public async Task WhenAUserIsLockedOutAfterFailedSignIns_ThenTheyKeepTheirRoles()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.OperatorName]);

        // Act
        await graph.SetLockoutEndAsync("mom", graph.Time.GetUtcNow().AddMinutes(5));

        // Assert
        Assert.Equal([BuiltInRoles.OperatorName], graph.UserStore.GetRoles(graph.UserStore.GetUserId("mom")!));
    }

    [Fact]
    public async Task WhenTheActorIsLockedOutAfterFailedSignIns_ThenTheyKeepManagingUsersAndTheirOwnAccount()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await graph.AddUserAsync("boss", [BuiltInRoles.EverythingName]);
        await graph.SetLockoutEndAsync("admin", graph.Time.GetUtcNow().AddMinutes(5));

        // Act
        var added = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        var locked = await administration.SetLockedAsync("boss", true, CancellationToken.None);
        var ownDisplayName = await graph.UserStore.SetOwnDisplayNameAsync(graph.AdminUserId, "Admin", CancellationToken.None);

        // Assert
        Assert.True(graph.Authorization.Users["admin"].IsLockedOut);
        Assert.True(added.Succeeded, added.Error);
        Assert.True(locked.Succeeded, locked.Error);
        Assert.True(ownDisplayName.Succeeded, ownDisplayName.Error);
    }

    [Fact]
    public async Task WhenASignInIsRecorded_ThenTheLastLoginIsStoredAndShown()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var changed = new List<string>();
        graph.UserStore.UserChanged += changed.Add;

        // Act
        await graph.UserStore.RecordSignInAsync(graph.AdminUserId, CancellationToken.None);

        // Assert
        var now = graph.Time.GetUtcNow();
        Assert.Equal(now, graph.Authorization.Users["admin"].LastLogin);
        Assert.Equal(now, (await graph.UserManager.FindByIdAsync(graph.AdminUserId))!.LastLogin);
        Assert.Equal([graph.AdminUserId], changed);
    }

    [Fact]
    public async Task WhenIdentityLocksAUserOutOutsideTheStore_ThenRefreshingTheUserShowsTheLockout()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var userManager = graph.UserManager;
        var admin = (await userManager.FindByIdAsync(graph.AdminUserId))!;
        Assert.True((await userManager.SetLockoutEndDateAsync(admin, graph.Time.GetUtcNow().AddMinutes(5))).Succeeded);
        Assert.False(graph.Authorization.Users["admin"].IsLockedOut);

        // Act
        await graph.UserStore.RefreshUserAsync(graph.AdminUserId, CancellationToken.None);

        // Assert
        Assert.True(graph.Authorization.Users["admin"].IsLockedOut);
        Assert.Null(graph.Authorization.Users["admin"].LastLogin);
    }

    [Fact]
    public async Task WhenAPasswordIsReset_ThenASetupCodeIsReturnedAndThePasswordMustChange()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", []);

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).ResetPasswordAsync("mom", CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.False(string.IsNullOrEmpty(result.SetupCode));
        Assert.True(graph.Authorization.Users["mom"].MustChangePassword);
    }

    [Fact]
    public async Task WhenARoleAUserHoldsIsRenamed_ThenTheUserHoldsTheNewName()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.ReaderName] }, null, CancellationToken.None);
        await graph.AddUserAsync("mom", ["family"]);

        // Act
        var result = await graph.RoleStore.RenameRoleAsync("family", "household", CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["household"], graph.UserStore.GetRoles(graph.UserStore.GetUserId("mom")!));
        Assert.Equal(["household"], graph.Authorization.Users["mom"].Roles);
    }

    [Fact]
    public async Task WhenAManagerWithPathRulesManagesUsers_ThenUsersBelowThemCanBeAddedChangedLockedAndRemoved()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, PathUserManagerRole(), null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager"]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));

        // Act
        var added = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        var rolesSet = await administration.SetRolesAsync("kid", [BuiltInRoles.OperatorName], CancellationToken.None);
        var locked = await administration.SetLockedAsync("kid", true, CancellationToken.None);
        var isLockedOut = graph.Authorization.Users["kid"].IsLockedOut;
        var removed = await administration.RemoveAsync("kid", CancellationToken.None);

        // Assert
        Assert.True(added.Succeeded, added.Error);
        Assert.True(rolesSet.Succeeded, rolesSet.Error);
        Assert.True(locked.Succeeded, locked.Error);
        Assert.True(isLockedOut);
        Assert.True(removed.Succeeded, removed.Error);
        Assert.Null(graph.UserStore.GetUserId("kid"));
    }

    [Fact]
    public async Task WhenAManagerWithPathRulesChangesTheirOwnRolesOrAdmin_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, PathUserManagerRole(), null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager"]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));

        // Act
        var own = await administration.SetRolesAsync("manager", ["usermanager", BuiltInRoles.OperatorName], CancellationToken.None);
        var adminRoles = await administration.SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);
        var adminLock = await administration.SetLockedAsync("admin", true, CancellationToken.None);

        // Assert
        Assert.Equal("You cannot change your own roles.", own.Error);
        Assert.Equal("This user holds roles you do not hold: everything.", adminRoles.Error);
        Assert.Equal("This user holds roles you do not hold: everything.", adminLock.Error);
        Assert.Equal(["usermanager"], graph.UserStore.GetRoles(graph.UserStore.GetUserId("manager")!));
        Assert.False(graph.Authorization.Users["admin"].IsLockedOut);
    }

    [Fact]
    public async Task WhenOneOperationIsDeniedToAManager_ThenTheStoreRefusesItButAllowsTheOthers()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var role = PathUserManagerRole() with
        {
            Deny = [new PermissionRule { Paths = ["/Authorization/Users/**"], Members = ["Remove"], Actions = [AuthorizationActions.Administer] }]
        };
        await graph.RoleStore.SaveRoleAsync(null, role, null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager"]);
        await graph.AddUserAsync("kid", [BuiltInRoles.ReaderName]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));

        // Act
        var removed = await administration.RemoveAsync("kid", CancellationToken.None);
        var locked = await administration.SetLockedAsync("kid", true, CancellationToken.None);

        // Assert
        Assert.Equal("You are not allowed to manage the user 'kid'.", removed.Error);
        Assert.NotNull(graph.UserStore.GetUserId("kid"));
        Assert.True(locked.Succeeded, locked.Error);
    }

    [Fact]
    public async Task WhenAPersonWithoutAdministerNamesAMissingUser_ThenTheRefusalDoesNotTellWhetherItExists()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("dad", [BuiltInRoles.OperatorName]);
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var dad = graph.AdministrationAs(graph.UserStore.GetUserId("dad"));

        // Act
        var missing = await dad.RemoveAsync("ghost", CancellationToken.None);
        var existing = await dad.RemoveAsync("mom", CancellationToken.None);
        var missingForAdmin = await graph.AdministrationAs(graph.AdminUserId).RemoveAsync("ghost", CancellationToken.None);

        // Assert
        Assert.Equal("You are not allowed to manage the user 'ghost'.", missing.Error);
        Assert.Equal("You are not allowed to manage the user 'mom'.", existing.Error);
        Assert.Equal("The user 'ghost' does not exist.", missingForAdmin.Error);
    }

    [Fact]
    public async Task WhenARoleNameIsNullOrBlank_ThenTheChangeIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);

        // Act
        var added = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName, null!], CancellationToken.None);
        var rolesSet = await administration.SetRolesAsync("mom", [" "], CancellationToken.None);

        // Assert
        Assert.Equal("A role name cannot be empty.", added.Error);
        Assert.Equal("A role name cannot be empty.", rolesSet.Error);
        Assert.Null(graph.UserStore.GetUserId("kid"));
        Assert.Equal([BuiltInRoles.ReaderName], graph.UserStore.GetRoles(graph.UserStore.GetUserId("mom")!));
    }

    [Fact]
    public async Task WhenTheLastFullAdministratorIsRemovedOrLocked_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        var expected = "'admin' is the last unlocked full administrator; give another user everything without a role that denies administer first.";

        // Act
        var removed = await administration.RemoveAsync("admin", CancellationToken.None);
        var locked = await administration.SetLockedAsync("admin", true, CancellationToken.None);

        // Assert
        Assert.Equal(expected, removed.Error);
        Assert.Equal(expected, locked.Error);
        Assert.False(graph.Authorization.Users["admin"].IsLockedOut);
    }

    [Fact]
    public async Task WhenTheOnlyOtherFullAdministratorIsLocked_ThenTheyDoNotCount()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await graph.AddUserAsync("boss", [BuiltInRoles.EverythingName]);
        await administration.SetLockedAsync("boss", true, CancellationToken.None);

        // Act
        var result = await administration.SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.Equal("'admin' is the last unlocked full administrator; give another user everything without a role that denies administer first.", result.Error);
    }

    [Fact]
    public async Task WhenTheLastFullAdministratorWouldGetARoleThatDeniesAdminister_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await SaveRoleAsync(graph, NoAdministrationRole());
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var result = await administration.SetRolesAsync("admin", [BuiltInRoles.EverythingName, NoAdministrationRole().Name], CancellationToken.None);

        // Assert
        Assert.Equal("'admin' is the last unlocked full administrator; give another user everything without a role that denies administer first.", result.Error);
        Assert.Equal([BuiltInRoles.EverythingName], graph.UserStore.GetRoles(graph.AdminUserId));
    }

    [Fact]
    public async Task WhenTheOnlyOtherUserWithEverythingHasARoleThatDeniesAdminister_ThenTheyDoNotCount()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await SaveRoleAsync(graph, NoAdministrationRole());
        await graph.AddUserAsync("boss", [BuiltInRoles.EverythingName, NoAdministrationRole().Name]);
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var demoted = await administration.SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);
        var removed = await administration.RemoveAsync("admin", CancellationToken.None);
        var locked = await administration.SetLockedAsync("admin", true, CancellationToken.None);

        // Assert
        var expected = "'admin' is the last unlocked full administrator; give another user everything without a role that denies administer first.";
        Assert.Equal(expected, demoted.Error);
        Assert.Equal(expected, removed.Error);
        Assert.Equal(expected, locked.Error);
        Assert.Equal([BuiltInRoles.EverythingName], graph.UserStore.GetRoles(graph.AdminUserId));
    }

    [Fact]
    public async Task WhenTheOnlyOtherFullAdministratorIsLockedOutAfterFailedSignIns_ThenTheyCount()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await graph.AddUserAsync("boss", [BuiltInRoles.EverythingName]);
        await graph.SetLockoutEndAsync("boss", graph.Time.GetUtcNow().AddMinutes(5));

        // Act
        var result = await administration.SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task WhenNoAuthorizationSubjectIsAttached_ThenEvenAFullAdministratorIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        graph.DetachProjection();
        var administration = graph.AdministrationAs(graph.AdminUserId);

        // Act
        var added = await administration.AddUserAsync("kid", null, [], CancellationToken.None);
        var removed = await administration.RemoveAsync("mom", CancellationToken.None);

        // Assert
        Assert.Equal("You are not allowed to add users.", added.Error);
        Assert.Equal("You are not allowed to manage the user 'mom'.", removed.Error);
        Assert.NotNull(graph.UserStore.GetUserId("mom"));
    }

    [Fact]
    public async Task WhenTheAttachedSubjectIsAttachedAgain_ThenItsUsersAreKept()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var mom = graph.Authorization.Users["mom"];
        var roles = graph.Authorization.Roles.Values.ToList();

        // Act
        graph.AttachProjectionAgain();

        // Assert
        Assert.Same(mom, graph.Authorization.Users["mom"]);
        Assert.Equal(roles, graph.Authorization.Roles.Values.ToList());
    }

    [Fact]
    public async Task WhenTheSetupCodeOfAResetCannotBeStored_ThenThePasswordAndStampAreUnchanged()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", []);
        await graph.SetPasswordAsync("mom", "Correct-Horse-1");
        var momId = graph.UserStore.GetUserId("mom")!;
        var stampBefore = await graph.GetSecurityStampAsync(momId);
        var (_, _, tokensBefore) = await graph.GetStoredUserAsync("mom", "Correct-Horse-1");
        graph.FailUserUpdatesWhen = user => user.MustChangePassword;

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).ResetPasswordAsync("mom", CancellationToken.None);

        // Assert
        graph.FailUserUpdatesWhen = null;
        Assert.False(result.Succeeded);
        Assert.Null(result.SetupCode);
        Assert.Contains("The setup code could not be stored", result.Error);
        var (user, isPassword, tokensAfter) = await graph.GetStoredUserAsync("mom", "Correct-Horse-1");
        Assert.True(isPassword);
        Assert.False(user.MustChangePassword);
        Assert.Equal(tokensBefore, tokensAfter);
        Assert.Equal(stampBefore, await graph.GetSecurityStampAsync(momId));
        Assert.False(graph.Authorization.Users["mom"].MustChangePassword);
    }

    [Fact]
    public async Task WhenTheAdminIsSeeded_ThenTheSeededAdministratorIsUnclaimed()
    {
        // Arrange & Act
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Assert
        Assert.True(graph.UserStore.IsSeededAdministratorUnclaimed);
    }

    [Fact]
    public async Task WhenAUserAllowsAnEmptyPasswordButNeedNotChangeIt_ThenTheirSessionGoesOnButMustChangeIt()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var userManager = graph.UserManager;
        var admin = (await userManager.FindByIdAsync(graph.AdminUserId))!;
        admin.MustChangePassword = false;
        Assert.True((await userManager.UpdateAsync(admin)).Succeeded);
        await graph.UserStore.RefreshUserAsync(graph.AdminUserId, CancellationToken.None);
        var stamp = await graph.GetSecurityStampAsync(graph.AdminUserId);

        // Act
        var isSessionAllowed = graph.UserStore.IsSessionAllowed(graph.AdminUserId, stamp);
        var requiresPasswordChange = graph.UserStore.RequiresPasswordChange(graph.AdminUserId);

        // Assert
        Assert.True(isSessionAllowed);
        Assert.True(requiresPasswordChange);
    }

    [Fact]
    public async Task WhenAPersonSetsTheirPasswordOrIsUnknown_ThenOnlyTheUnknownOneMustChangeIt()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var momId = graph.UserStore.GetUserId("mom")!;
        var requiredBefore = graph.UserStore.RequiresPasswordChange(momId);

        // Act
        await graph.SetPasswordAsync("mom", "Correct-Horse-1");
        var requiredAfter = graph.UserStore.RequiresPasswordChange(momId);
        var requiredOfUnknown = graph.UserStore.RequiresPasswordChange("unknown");

        // Assert
        Assert.True(requiredBefore);
        Assert.False(requiredAfter);
        Assert.True(requiredOfUnknown);
    }

    [Fact]
    public async Task WhenTheAdminNoLongerAllowsAnEmptyPassword_ThenRefreshingTheUserClaimsTheAdministrator()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var changed = new List<string>();
        graph.UserStore.UserChanged += changed.Add;
        var userManager = graph.UserManager;
        var admin = (await userManager.FindByIdAsync(graph.AdminUserId))!;
        admin.AllowsEmptyPassword = false;
        Assert.True((await userManager.UpdateAsync(admin)).Succeeded);

        // Act
        await graph.UserStore.RefreshUserAsync(graph.AdminUserId, CancellationToken.None);

        // Assert
        Assert.False(graph.UserStore.IsSeededAdministratorUnclaimed);
        Assert.Equal([graph.AdminUserId], changed);
    }

    [Fact]
    public async Task WhenTheAdminIsReset_ThenTheSeededAdministratorIsUnclaimedAgain()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.EverythingName]);
        var momId = graph.UserStore.GetUserId("mom")!;
        var reset = await graph.AdministrationAs(momId).ResetPasswordAsync(AdminSeeding.AdminUserName, CancellationToken.None);
        Assert.True(reset.Succeeded, reset.Error);
        Assert.False(graph.UserStore.IsSeededAdministratorUnclaimed);

        // Act
        await graph.ResetAdminAsync();

        // Assert
        Assert.True(graph.UserStore.IsSeededAdministratorUnclaimed);
    }

    [Fact]
    public async Task WhenAManagerHoldsARoleWithDenyRules_ThenTheUsersTheyAddOrActOnMustHoldIt()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, PathUserManagerRole(), null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, KidRole(), null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager", "kid"]);
        await graph.AddUserAsync("peer", [BuiltInRoles.OperatorName]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));

        // Act
        var withoutKid = await administration.AddUserAsync("guest", null, [BuiltInRoles.OperatorName], CancellationToken.None);
        var withKid = await administration.AddUserAsync("child", null, [BuiltInRoles.OperatorName, "kid"], CancellationToken.None);
        var resetPeer = await administration.ResetPasswordAsync("peer", CancellationToken.None);
        var restrictPeer = await administration.SetRolesAsync("peer", ["kid"], CancellationToken.None);
        var resetChild = await administration.ResetPasswordAsync("child", CancellationToken.None);

        // Assert
        Assert.Equal("The assigned roles must include your roles with deny rules: kid.", withoutKid.Error);
        Assert.Null(graph.UserStore.GetUserId("guest"));
        Assert.True(withKid.Succeeded, withKid.Error);
        Assert.Equal("This user does not hold your roles with deny rules: kid.", resetPeer.Error);
        Assert.Equal("This user does not hold your roles with deny rules: kid.", restrictPeer.Error);
        Assert.Equal([BuiltInRoles.OperatorName], graph.UserStore.GetRoles(graph.UserStore.GetUserId("peer")!));
        Assert.True(resetChild.Succeeded, resetChild.Error);
    }

    [Fact]
    public async Task WhenAnAdministratorSetsADisplayName_ThenItIsTrimmedStoredAndShownWithoutEndingSessions()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var momId = graph.UserStore.GetUserId("mom")!;
        var stampBefore = await graph.GetSecurityStampAsync(momId);
        var changed = new List<string>();
        graph.UserStore.UserChanged += changed.Add;

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).SetDisplayNameAsync("mom", "  Mom M\u00fcller ", CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Mom M\u00fcller", graph.Authorization.Users["mom"].DisplayName);
        Assert.Equal("Mom M\u00fcller", graph.UserStore.GetDisplayName(momId));
        Assert.Contains(momId, changed);
        Assert.Equal(stampBefore, await graph.GetSecurityStampAsync(momId));
        await graph.UserStore.LoadAsync(CancellationToken.None);
        Assert.Equal("Mom M\u00fcller", graph.Authorization.Users["mom"].DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task WhenAnEmptyDisplayNameIsSet_ThenTheUserHasNone(string? displayName)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);
        var administration = graph.AdministrationAs(graph.AdminUserId);
        await administration.SetDisplayNameAsync("mom", "Mom", CancellationToken.None);

        // Act
        var result = await administration.SetDisplayNameAsync("mom", displayName, CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Null(graph.Authorization.Users["mom"].DisplayName);
        await graph.UserStore.LoadAsync(CancellationToken.None);
        Assert.Null(graph.Authorization.Users["mom"].DisplayName);
    }

    [Fact]
    public async Task WhenADisplayNameIsInvalid_ThenItIsRefusedAndNothingChanges()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.ReaderName]);

        // Act
        var bidi = await graph.AdministrationAs(graph.AdminUserId).SetDisplayNameAsync("mom", "Mom\u202E", CancellationToken.None);
        var tooLong = await graph.UserStore.SetOwnDisplayNameAsync(graph.UserStore.GetUserId("mom")!, new string('a', 101), CancellationToken.None);

        // Assert
        Assert.Equal("The display name contains a control, invisible, private use or unassigned character.", bidi.Error);
        Assert.Equal("A display name has at most 100 characters.", tooLong.Error);
        Assert.Null(graph.Authorization.Users["mom"].DisplayName);
    }

    [Theory]
    [InlineData(NameRules.AnonymousUserName)]
    [InlineData(NameRules.McpUserName)]
    public async Task WhenAFullAdministratorSetsTheDisplayNameOfABuiltInUser_ThenItIsRefused(string userName)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).SetDisplayNameAsync(userName, "Visitors", CancellationToken.None);

        // Assert
        Assert.Equal($"The built-in user '{userName}' has no display name.", result.Error);
        Assert.Null(graph.Authorization.Users[userName].DisplayName);
    }

    [Fact]
    public async Task WhenAManagerSetsDisplayNames_ThenTheDelegationRulesDecide()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, PathUserManagerRole(), null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager"]);
        await graph.AddUserAsync("kid", [BuiltInRoles.ReaderName]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));

        // Act
        var kid = await administration.SetDisplayNameAsync("kid", "Kid", CancellationToken.None);
        var own = await administration.SetDisplayNameAsync("manager", "Manager", CancellationToken.None);
        var admin = await administration.SetDisplayNameAsync("admin", "Boss", CancellationToken.None);
        var mcp = await administration.SetDisplayNameAsync(NameRules.McpUserName, "Clients", CancellationToken.None);

        // Assert
        Assert.True(kid.Succeeded, kid.Error);
        Assert.Equal("Kid", graph.Authorization.Users["kid"].DisplayName);
        Assert.Equal("You cannot do this to your own account.", own.Error);
        Assert.Equal("This user holds roles you do not hold: everything.", admin.Error);
        Assert.False(mcp.Succeeded);
        Assert.Null(graph.Authorization.Users["admin"].DisplayName);
    }

    [Fact]
    public async Task WhenAPersonWithoutAdministerSetsADisplayName_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", [BuiltInRoles.OperatorName]);
        await graph.AddUserAsync("kid", [BuiltInRoles.ReaderName]);

        // Act
        var result = await graph.AdministrationAs(graph.UserStore.GetUserId("mom")).SetDisplayNameAsync("kid", "Kid", CancellationToken.None);

        // Assert
        Assert.Equal("You are not allowed to manage the user 'kid'.", result.Error);
        Assert.Null(graph.Authorization.Users["kid"].DisplayName);
    }

    [Fact]
    public async Task WhenAPersonSetsTheirOwnDisplayName_ThenItIsSetWithoutAdministration()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", []);
        var momId = graph.UserStore.GetUserId("mom")!;

        // Act
        var result = await graph.UserStore.SetOwnDisplayNameAsync(momId, "\u0645\u0627\u0645\u0627\u0646", CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("\u0645\u0627\u0645\u0627\u0646", graph.Authorization.Users["mom"].DisplayName);
    }

    [Fact]
    public async Task WhenAnUnknownLockedOrBuiltInUserSetsTheirOwnDisplayName_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("mom", []);
        await graph.SetLockoutEndAsync("mom", DateTimeOffset.MaxValue);

        // Act
        var unknown = await graph.UserStore.SetOwnDisplayNameAsync("unknown-id", "Ghost", CancellationToken.None);
        var locked = await graph.UserStore.SetOwnDisplayNameAsync(graph.UserStore.GetUserId("mom")!, "Mom", CancellationToken.None);
        var builtIn = await graph.UserStore.SetOwnDisplayNameAsync(graph.UserStore.GetUserId(NameRules.AnonymousUserName)!, "Visitors", CancellationToken.None);

        // Assert
        Assert.False(unknown.Succeeded);
        Assert.False(locked.Succeeded);
        Assert.False(builtIn.Succeeded);
        Assert.Null(graph.Authorization.Users["mom"].DisplayName);
        Assert.Null(graph.Authorization.Users[NameRules.AnonymousUserName].DisplayName);
    }

    internal static AuthorizationRole PathUserManagerRole() => new()
    {
        Name = "usermanager",
        Includes = [BuiltInRoles.OperatorName],
        Allow =
        [
            new PermissionRule { Paths = ["/Authorization/Users/**"], Actions = [AuthorizationActions.Administer] },
            new PermissionRule { Paths = ["/Authorization/AddUser"], Actions = [AuthorizationActions.Administer] }
        ]
    };

    private static async Task SaveRoleAsync(AuthenticationTestGraph graph, AuthorizationRole role)
    {
        var saved = await graph.RoleStore.SaveRoleAsync(null, role, null, CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error);
    }

    // Denies administer on one branch only, which still takes full administration away.
    private static AuthorizationRole NoAdministrationRole() => new()
    {
        Name = "noadmin",
        Deny = [new PermissionRule { Paths = ["/Heating/**"], Actions = [AuthorizationActions.Administer] }]
    };

    // Includes Operator and denies writing below /Heating.
    private static AuthorizationRole KidRole() => new()
    {
        Name = "kid",
        Includes = [BuiltInRoles.OperatorName],
        Deny = [new PermissionRule { Paths = ["/Heating/**"], Actions = [PermissionAction.Write] }]
    };

    private static AuthorizationRole UserManagerRole() => new()
    {
        Name = "usermanager",
        Includes = [BuiltInRoles.OperatorName],
        Allow =
        [
            new PermissionRule { Types = [typeof(User).FullName!], Actions = [AuthorizationActions.Administer] },
            new PermissionRule { Types = [typeof(AuthorizationSubject).FullName!], Members = ["AddUser"], Actions = [AuthorizationActions.Administer] }
        ]
    };
}
