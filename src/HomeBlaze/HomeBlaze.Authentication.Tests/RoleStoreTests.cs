using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class RoleStoreTests
{
    [Fact]
    public async Task WhenARoleIsSaved_ThenItIsProjectedAndCompiles()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.OperatorName] }, null, CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.True(graph.Authorization.Roles.ContainsKey("family"));
        Assert.Null(graph.AuthorizationService.GetPolicy(["family"], PrincipalKind.Person).Error);
    }

    [Fact]
    public async Task WhenASaveWouldBreakAPrincipal_ThenItIsRefusedAndNothingChanges()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.OperatorName] }, null, CancellationToken.None);
        graph.AuthorizationService.SetPrincipalRoles(new object(), ["family", BuiltInRoles.EverythingName]);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(
            "family",
            new AuthorizationRole { Name = "family", Includes = ["DoesNotExist"] },
            graph.RoleStore.GetConcurrencyStamp("family"),
            CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("DoesNotExist", result.Error);
        Assert.Equal([BuiltInRoles.OperatorName], graph.Authorization.Roles["family"].Includes);
    }

    [Fact]
    public async Task WhenTheStampIsStale_ThenTheSaveIsRefusedAsChanged()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        var stamp = graph.RoleStore.GetConcurrencyStamp("family");
        await graph.RoleStore.SaveRoleAsync("family", new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.ReaderName] }, stamp, CancellationToken.None);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync("family", new AuthorizationRole { Name = "family" }, stamp, CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("changed since you opened it", result.Error);
    }

    [Fact]
    public async Task WhenARoleIsRenamed_ThenAgentsNamingItAreUpdated()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        var agentRoles = graph.AddAgentPrincipal(["family"]);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync("family", new AuthorizationRole { Name = "household" }, graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["household"], agentRoles.Current);
    }

    [Fact]
    public async Task WhenAHandlerOfTheRenameThrows_ThenTheRenameIsProjectedAndReportedWithAWarning()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        graph.RoleStore.RoleRenamed += (_, _) => throw new InvalidOperationException("The users failed.");

        // Act
        var result = await graph.RoleStore.RenameRoleAsync("family", "household", CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("The users failed.", result.Warning);
        Assert.Equal(["household"], GetCustomRoleNames(graph));
        Assert.NotNull(graph.RoleStore.GetConcurrencyStamp("household"));
        Assert.Null(graph.RoleStore.GetConcurrencyStamp("family"));
    }

    [Fact]
    public async Task WhenARoleInUseIsDeleted_ThenTheDeleteIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        graph.AddAgentPrincipal(["family"]);

        // Act
        var result = await graph.RoleStore.DeleteRoleAsync("family", graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.True(graph.Authorization.Roles.ContainsKey("family"));
    }

    [Fact]
    public async Task WhenARoleWithRulesIsSaved_ThenItsRulesAreReadBackFromTheDatabase()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var allow = new PermissionRule { Paths = ["/Devices/**"], Tags = [MemberTags.State], Actions = [PermissionAction.Read, AuthorizationActions.History] };
        var deny = new PermissionRule { Members = ["Secret*"], Actions = [PermissionAction.Write] };

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Allow = [allow], Deny = [deny] }, null, CancellationToken.None);
        await graph.RoleStore.LoadAsync(CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        var role = graph.Authorization.Roles["family"];
        Assert.Equal([allow], role.Allow);
        Assert.Equal([deny], role.Deny);
        Assert.Empty(role.Includes);
    }

    [Fact]
    public async Task WhenABuiltInRoleRowIsDeletedOrRenamed_ThenItIsRefusedAndKept()
    {
        // Arrange: the graph seeds a row per built-in role
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        var deleteResult = await graph.RoleStore.DeleteRoleAsync(BuiltInRoles.ReaderName, null, CancellationToken.None);
        var renameResult = await graph.RoleStore.SaveRoleAsync(BuiltInRoles.ReaderName, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);

        // Assert
        Assert.False(deleteResult.Succeeded);
        Assert.False(renameResult.Succeeded);
        await using var verification = graph.CreateDbContext();
        var row = Assert.Single(verification.Roles, role => role.NormalizedName == BuiltInRoles.ReaderName.ToUpperInvariant());
        Assert.Equal(BuiltInRoles.ReaderName, row.Name);
        Assert.True(row.IsBuiltIn);
        Assert.DoesNotContain(verification.Roles, role => role.Name == "family");
        Assert.Empty(GetCustomRoleNames(graph));
    }

    [Fact]
    public async Task WhenARoleIsAddedUnderATakenName_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        var upperCase = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "FAMILY" }, null, CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Error);
        Assert.Equal(NameRules.GetNameError("FAMILY", "role name"), upperCase.Error);
        Assert.Equal(["family"], await graph.GetStoredRoleNamesAsync());
    }

    [Theory]
    [InlineData("Caf\u00e9")]
    [InlineData("Cafe\u0301")]
    [InlineData("Family")]
    [InlineData("family room")]
    public async Task WhenARoleNameBreaksTheNameRule_ThenTheSaveAndTheRenameAreRefused(string name)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);

        // Act
        var saved = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = name }, null, CancellationToken.None);
        var renamed = await graph.RoleStore.RenameRoleAsync("family", name, CancellationToken.None);

        // Assert
        Assert.Equal(NameRules.GetNameError(name, "role name"), saved.Error);
        Assert.Equal(NameRules.GetNameError(name, "role name"), renamed.Error);
        Assert.Equal(["family"], await graph.GetStoredRoleNamesAsync());
    }

    [Fact]
    public async Task WhenAnUnusedRoleIsDeleted_ThenItIsGone()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);

        // Act
        var result = await graph.RoleStore.DeleteRoleAsync("family", graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(GetCustomRoleNames(graph));
        Assert.Null(graph.RoleStore.GetConcurrencyStamp("family"));
    }

    [Fact]
    public async Task WhenASaveWouldBreakAnAgent_ThenTheRefusalNamesTheAgent()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.ReaderName] }, null, CancellationToken.None);
        graph.AddAgentPrincipal("kitchen helper", ["family"]);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync("family", new AuthorizationRole { Name = "family", Includes = ["missing"] }, graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("kitchen helper", result.Error);
    }

    [Fact]
    public async Task WhenAnAgentNamesAMissingRole_ThenItBlocksNeitherAnUnrelatedSaveNorCreatingTheRole()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var agent = graph.AddAgentPrincipal(["gone", BuiltInRoles.ReaderName]);

        // Act
        var unrelated = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        var repair = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "gone", Includes = [BuiltInRoles.ReaderName] }, null, CancellationToken.None);

        // Assert
        Assert.True(unrelated.Succeeded, unrelated.Error);
        Assert.True(repair.Succeeded, repair.Error);
        Assert.Null(graph.AuthorizationService.GetPolicy(agent.Current, PrincipalKind.Person).Error);
    }

    [Fact]
    public async Task WhenARoleIncludedByAHeldRoleIsRenamed_ThenTheIncludingRoleIncludesTheNewNameAndNothingBreaks()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "base", Includes = [BuiltInRoles.ReaderName] }, null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = ["base", BuiltInRoles.OperatorName] }, null, CancellationToken.None);
        var familyStamp = graph.RoleStore.GetConcurrencyStamp("family");
        var agent = graph.AddAgentPrincipal(["family"]);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync("base", new AuthorizationRole { Name = "core", Includes = [BuiltInRoles.ReaderName] }, graph.RoleStore.GetConcurrencyStamp("base"), CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["core", BuiltInRoles.OperatorName], graph.Authorization.Roles["family"].Includes);
        Assert.NotEqual(familyStamp, graph.RoleStore.GetConcurrencyStamp("family"));
        Assert.Null(graph.AuthorizationService.GetPolicy(agent.Current, PrincipalKind.Person).Error);

        // Act: the store reads the cascaded includes back from the database.
        await graph.RoleStore.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["core", BuiltInRoles.OperatorName], graph.Authorization.Roles["family"].Includes);
    }

    [Fact]
    public async Task WhenARoleIsSaved_ThenTheResultHoldsItsNewStamp()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);

        // Assert
        Assert.NotNull(result.ConcurrencyStamp);
        Assert.Equal(graph.RoleStore.GetConcurrencyStamp("family"), result.ConcurrencyStamp);
    }

    [Fact]
    public async Task WhenRenamingInAnAgentFailsAfterTheCommit_ThenTheRenameSucceedsWithAWarning()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        graph.AddAgentPrincipal(["family"]);
        graph.FailRenames = true;

        // Act
        var result = await graph.RoleStore.SaveRoleAsync("family", new AuthorizationRole { Name = "household" }, graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("family", result.Warning);
        Assert.Contains("disk is full", result.Warning);
        Assert.True(graph.Authorization.Roles.ContainsKey("household"));
        Assert.Equal(["household"], await graph.GetStoredRoleNamesAsync());
    }

    [Fact]
    public async Task WhenADeleteHasAStaleStamp_ThenItIsRefusedAndTheRowSurvives()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        var stamp = graph.RoleStore.GetConcurrencyStamp("family");
        await graph.RoleStore.SaveRoleAsync("family", new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.ReaderName] }, stamp, CancellationToken.None);

        // Act
        var result = await graph.RoleStore.DeleteRoleAsync("family", stamp, CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("changed since you opened it", result.Error);
        Assert.Equal(["family"], await graph.GetStoredRoleNamesAsync());
    }

    [Fact]
    public async Task WhenADeleteOfARoleAnAgentUsesIsRefused_ThenTheRowSurvives()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        graph.AddAgentPrincipal(["family"]);

        // Act
        var result = await graph.RoleStore.DeleteRoleAsync("family", graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(["family"], await graph.GetStoredRoleNamesAsync());
    }

    [Fact]
    public async Task WhenAUserHoldsTheRole_ThenTheDeleteIsRefusedAndTheRowSurvives()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        await graph.AddUserAsync("alice", ["family"]);

        // Act
        var result = await graph.RoleStore.DeleteRoleAsync("family", graph.RoleStore.GetConcurrencyStamp("family"), CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("used by users or agents", result.Error);
        Assert.Equal(["family"], await graph.GetStoredRoleNamesAsync());
        Assert.True(graph.Authorization.Roles.ContainsKey("family"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenARoleAndARoleItIncludesAreRenamed_ThenBothSucceedInEitherOrder(bool isIncludedRoleRenamedFirst)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var allow = new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] };
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "base", Includes = [BuiltInRoles.ReaderName] }, null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = ["base"], Allow = [allow] }, null, CancellationToken.None);
        var agent = graph.AddAgentPrincipal(["family"]);

        // Act
        var first = isIncludedRoleRenamedFirst
            ? await graph.RoleStore.RenameRoleAsync("base", "core", CancellationToken.None)
            : await graph.RoleStore.RenameRoleAsync("family", "household", CancellationToken.None);
        var second = isIncludedRoleRenamedFirst
            ? await graph.RoleStore.RenameRoleAsync("family", "household", CancellationToken.None)
            : await graph.RoleStore.RenameRoleAsync("base", "core", CancellationToken.None);

        // Assert
        Assert.True(first.Succeeded, first.Error);
        Assert.True(second.Succeeded, second.Error);
        var household = graph.Authorization.Roles["household"];
        Assert.Equal(["core"], household.Includes);
        Assert.Equal([allow], household.Allow);
        Assert.Equal(["household"], agent.Current);
        Assert.Null(graph.AuthorizationService.GetPolicy(agent.Current, PrincipalKind.Person).Error);
        Assert.Equal(["core", "household"], (await graph.GetStoredRoleNamesAsync()).Order());
    }

    [Fact]
    public async Task WhenARenamedRoleIsGoneOrTheNameIsTaken_ThenTheRenameIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family" }, null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "guests" }, null, CancellationToken.None);

        // Act
        var gone = await graph.RoleStore.RenameRoleAsync("gone", "household", CancellationToken.None);
        var taken = await graph.RoleStore.RenameRoleAsync("family", "guests", CancellationToken.None);
        var invalid = await graph.RoleStore.RenameRoleAsync("family", BuiltInRoles.ReaderName, CancellationToken.None);

        // Assert
        Assert.Contains("no longer exists", gone.Error);
        Assert.Contains("already exists", taken.Error);
        Assert.Contains("reserved", invalid.Error);
        Assert.Equal(["family", "guests"], (await graph.GetStoredRoleNamesAsync()).Order());
    }

    [Fact]
    public async Task WhenACascadedRoleChangedInTheDatabase_ThenTheRenameIsRefusedNamingItAndBothRowsStay()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "base" }, null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = ["base"] }, null, CancellationToken.None);
        await using (var context = graph.CreateDbContext())
        {
            var family = context.Roles.Single(role => role.NormalizedName == "FAMILY");
            family.ConcurrencyStamp = "changed elsewhere";
            await context.SaveChangesAsync();
        }

        // Act
        var result = await graph.RoleStore.RenameRoleAsync("base", "core", CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("The role 'family' changed since you opened it.", result.Error);
        Assert.Equal(["base", "family"], (await graph.GetStoredRoleNamesAsync()).Order());
        await graph.RoleStore.LoadAsync(CancellationToken.None);
        Assert.Equal(["base"], graph.Authorization.Roles["family"].Includes);
    }

    [Fact]
    public async Task WhenRolesAreSaved_ThenTheyAreProjectedOrderedByName()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();

        // Act
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "zeta" }, null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "beta" }, null, CancellationToken.None);
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "alpha" }, null, CancellationToken.None);

        // Assert
        Assert.Equal(["alpha", "beta", "zeta"], GetCustomRoleNames(graph));
    }

    [Fact]
    public async Task WhenTheOnlyFullAdministratorHoldsEverythingThroughARole_ThenRemovingItFromTheRoleIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.EverythingName] }, null, CancellationToken.None);
        await graph.AddUserAsync("boss", ["admins"]);
        var demoted = await graph.AdministrationAs(graph.AdminUserId).SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(
            "admins",
            new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.OperatorName] },
            graph.RoleStore.GetConcurrencyStamp("admins"),
            CancellationToken.None);

        // Assert
        Assert.True(demoted.Succeeded, demoted.Error);
        Assert.False(result.Succeeded);
        Assert.Equal("This change would leave no unlocked user with everything and no deny rule on administer; give another user everything first.", result.Error);
        Assert.Equal([BuiltInRoles.EverythingName], graph.Authorization.Roles["admins"].Includes);
    }

    [Fact]
    public async Task WhenTheRoleOfTheOnlyFullAdministratorWouldDenyAdminister_ThenTheChangeIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.EverythingName] }, null, CancellationToken.None);
        await graph.AddUserAsync("boss", ["admins"]);
        var demoted = await graph.AdministrationAs(graph.AdminUserId).SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(
            "admins",
            new AuthorizationRole
            {
                Name = "admins",
                Includes = [BuiltInRoles.EverythingName],
                Deny = [new PermissionRule { Types = ["Authorization"], Actions = [AuthorizationActions.Administer] }]
            },
            graph.RoleStore.GetConcurrencyStamp("admins"),
            CancellationToken.None);

        // Assert
        Assert.True(demoted.Succeeded, demoted.Error);
        Assert.False(result.Succeeded);
        Assert.Equal("This change would leave no unlocked user with everything and no deny rule on administer; give another user everything first.", result.Error);
        Assert.Empty(graph.Authorization.Roles["admins"].Deny);
    }

    [Fact]
    public async Task WhenTheOnlyFullAdministratorIsTemporarilyLockedOut_ThenRemovingEverythingFromTheirRoleIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.EverythingName] }, null, CancellationToken.None);
        await graph.AddUserAsync("boss", ["admins"]);
        var demoted = await graph.AdministrationAs(graph.AdminUserId).SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);
        await graph.SetLockoutEndAsync("boss", graph.Time.GetUtcNow().AddMinutes(5));

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(
            "admins",
            new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.OperatorName] },
            graph.RoleStore.GetConcurrencyStamp("admins"),
            CancellationToken.None);

        // Assert
        Assert.True(demoted.Succeeded, demoted.Error);
        Assert.True(graph.Authorization.Users["boss"].IsLockedOut);
        Assert.False(result.Succeeded);
        Assert.Equal("This change would leave no user with everything and no deny rule on administer; give another user everything first.", result.Error);
        Assert.Equal([BuiltInRoles.EverythingName], graph.Authorization.Roles["admins"].Includes);
    }

    [Fact]
    public async Task WhenTheOnlyFullAdministratorIsTemporarilyLockedOut_ThenAnUnrelatedRoleCanStillBeSaved()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("boss", [BuiltInRoles.EverythingName]);
        var demoted = await graph.AdministrationAs(graph.AdminUserId).SetRolesAsync("admin", [BuiltInRoles.OperatorName], CancellationToken.None);
        await graph.SetLockoutEndAsync("boss", graph.Time.GetUtcNow().AddMinutes(5));

        // Act
        var result = await graph.RoleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "guests", Includes = [BuiltInRoles.ReaderName] }, null, CancellationToken.None);

        // Assert
        Assert.True(demoted.Succeeded, demoted.Error);
        Assert.True(result.Succeeded, result.Error);
    }

    private static IEnumerable<string> GetCustomRoleNames(AuthenticationTestGraph graph) =>
        graph.Authorization.Roles.Where(entry => !entry.Value.IsBuiltIn).Select(entry => entry.Key);
}
