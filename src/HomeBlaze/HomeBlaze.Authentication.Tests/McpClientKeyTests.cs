using HomeBlaze.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class McpClientKeyTests
{
    [Fact]
    public void WhenTheKeyHelpersAreInspected_ThenOnlyThisAssemblyCanChangeAKeyPastTheCachedHash()
    {
        // Arrange
        var type = typeof(McpClientKey);

        // Act
        var isVisible = type.IsVisible;

        // Assert
        Assert.False(isVisible);
    }

    [Fact]
    public async Task WhenAKeyIsCreated_ThenOnlyThatKeyVerifies()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var mcp = (await graph.UserManager.FindByNameAsync(NameRules.McpUserName))!;

        // Act
        var key = await McpClientKey.CreateAsync(graph.UserManager, mcp);

        // Assert
        Assert.True(await McpClientKey.VerifyAsync(graph.UserManager, mcp, key));
        Assert.False(await McpClientKey.VerifyAsync(graph.UserManager, mcp, key + "x"));
    }

    [Fact]
    public async Task WhenTheKeyIsRevoked_ThenNoKeyIsRequiredAndNoneVerifies()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var mcp = (await graph.UserManager.FindByNameAsync(NameRules.McpUserName))!;
        var key = await McpClientKey.CreateAsync(graph.UserManager, mcp);

        // Act
        await McpClientKey.RevokeAsync(graph.UserManager, mcp);

        // Assert
        Assert.False(await McpClientKey.HasKeyAsync(graph.UserManager, mcp));
        Assert.False(await McpClientKey.VerifyAsync(graph.UserManager, mcp, key));
    }

    [Fact]
    public async Task WhenAKeyIsCreated_ThenItIsUrlSafeAndOnlyItsHashIsStored()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var mcp = (await graph.UserManager.FindByNameAsync(NameRules.McpUserName))!;

        // Act
        var first = await McpClientKey.CreateAsync(graph.UserManager, mcp);
        var second = await McpClientKey.CreateAsync(graph.UserManager, mcp);
        var stored = await graph.UserManager.GetAuthenticationTokenAsync(mcp, "HomeBlaze", "McpKey");

        // Assert
        Assert.Matches("^hb_[A-Za-z0-9_-]{43}$", second);
        Assert.NotEqual(first, second);
        Assert.NotNull(stored);
        Assert.DoesNotContain(second, stored);
        Assert.False(await McpClientKey.VerifyAsync(graph.UserManager, mcp, first));
        Assert.True(await McpClientKey.VerifyAsync(graph.UserManager, mcp, second));
    }

    [Fact]
    public async Task WhenNoKeyExists_ThenNoKeyIsRequiredAndNothingVerifies()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var access = (IMcpClientAccess)graph.UserStore;

        // Act
        var requiresKey = access.RequiresKey;
        var verifies = access.Verify(string.Empty);

        // Assert
        Assert.False(requiresKey);
        Assert.False(verifies);
        Assert.Equal([BuiltInRoles.ReaderName], access.Roles);
        Assert.False(graph.Authorization.Users[NameRules.McpUserName].HasKey);
    }

    [Fact]
    public async Task WhenAFullAdministratorCreatesTheKey_ThenTheStoreRequiresAndVerifiesIt()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var access = (IMcpClientAccess)graph.UserStore;
        var changed = new List<string>();
        graph.UserStore.UserChanged += changed.Add;

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).CreateMcpKeyAsync(CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.StartsWith("hb_", result.McpKey);
        Assert.True(access.RequiresKey);
        Assert.True(access.Verify(result.McpKey!));
        Assert.False(access.Verify(result.McpKey + "x"));
        Assert.True(graph.Authorization.Users[NameRules.McpUserName].HasKey);
        Assert.False(graph.Authorization.Users[AdminSeeding.AdminUserName].HasKey);
        Assert.Equal([graph.UserStore.GetUserId(NameRules.McpUserName)!], changed);
        var mcp = (await graph.UserManager.FindByNameAsync(NameRules.McpUserName))!;
        Assert.True(await McpClientKey.VerifyAsync(graph.UserManager, mcp, result.McpKey!));
    }

    [Fact]
    public async Task WhenAFullAdministratorRevokesTheKey_ThenNoKeyIsRequiredAndTheOldOneNoLongerVerifies()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var access = (IMcpClientAccess)graph.UserStore;
        var administration = graph.AdministrationAs(graph.AdminUserId);
        var created = await administration.CreateMcpKeyAsync(CancellationToken.None);

        // Act
        var result = await administration.RevokeMcpKeyAsync(CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.McpKey);
        Assert.False(access.RequiresKey);
        Assert.False(access.Verify(created.McpKey!));
        Assert.False(graph.Authorization.Users[NameRules.McpUserName].HasKey);
        var mcp = (await graph.UserManager.FindByNameAsync(NameRules.McpUserName))!;
        Assert.False(await McpClientKey.HasKeyAsync(graph.UserManager, mcp));
    }

    [Fact]
    public async Task WhenTheStoreLoads_ThenAKeyStoredBeforeIsRequired()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var mcp = (await graph.UserManager.FindByNameAsync(NameRules.McpUserName))!;
        var key = await McpClientKey.CreateAsync(graph.UserManager, mcp);
        var access = (IMcpClientAccess)graph.UserStore;
        var requiredBeforeLoad = access.RequiresKey;

        // Act
        await graph.UserStore.LoadAsync(CancellationToken.None);

        // Assert
        Assert.False(requiredBeforeLoad);
        Assert.True(access.RequiresKey);
        Assert.True(access.Verify(key));
        Assert.True(graph.Authorization.Users[NameRules.McpUserName].HasKey);
    }

    [Fact]
    public async Task WhenTheRolesOfTheMcpUserChange_ThenTheAccessHasTheNewRoles()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var access = (IMcpClientAccess)graph.UserStore;

        // Act
        await graph.AdministrationAs(graph.AdminUserId).SetRolesAsync(NameRules.McpUserName, [BuiltInRoles.OperatorName], CancellationToken.None);

        // Assert
        Assert.Equal([BuiltInRoles.OperatorName], access.Roles);
    }

    [Fact]
    public async Task WhenAPersonWhoIsNotAFullAdministratorManagesTheKey_ThenItIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.RoleStore.SaveRoleAsync(null, UserStoreTests.PathUserManagerRole(), null, CancellationToken.None);
        await graph.AddUserAsync("manager", ["usermanager"]);
        var administration = graph.AdministrationAs(graph.UserStore.GetUserId("manager"));
        var created = await graph.AdministrationAs(graph.AdminUserId).CreateMcpKeyAsync(CancellationToken.None);

        // Act
        var create = await administration.CreateMcpKeyAsync(CancellationToken.None);
        var revoke = await administration.RevokeMcpKeyAsync(CancellationToken.None);

        // Assert
        Assert.False(create.Succeeded);
        Assert.Null(create.McpKey);
        Assert.Equal("Only a full administrator can manage the key of the built-in user 'mcp'.", create.Error);
        Assert.False(revoke.Succeeded);
        Assert.Equal("Only a full administrator can manage the key of the built-in user 'mcp'.", revoke.Error);
        Assert.True(((IMcpClientAccess)graph.UserStore).Verify(created.McpKey!));
    }

    [Fact]
    public async Task WhenNobodyIsSignedIn_ThenManagingTheKeyIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(null);

        // Act
        var create = await administration.CreateMcpKeyAsync(CancellationToken.None);
        var revoke = await administration.RevokeMcpKeyAsync(CancellationToken.None);

        // Assert
        Assert.Equal(UserStore.ActorRefusal, create.Error);
        Assert.Equal(UserStore.ActorRefusal, revoke.Error);
        Assert.False(((IMcpClientAccess)graph.UserStore).RequiresKey);
    }

    [Fact]
    public async Task WhenNoAuthorizationSubjectIsAttached_ThenEvenAFullAdministratorCannotCreateTheKey()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        graph.DetachProjection();

        // Act
        var result = await graph.AdministrationAs(graph.AdminUserId).CreateMcpKeyAsync(CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("You are not allowed to manage the user 'mcp'.", result.Error);
        Assert.False(((IMcpClientAccess)graph.UserStore).RequiresKey);
    }

    [Fact]
    public async Task WhenTheOperationsOfTheMcpUserAreCalled_ThenTheKeyIsCreatedAndRevoked()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        var access = (IMcpClientAccess)graph.UserStore;

        // Act
        var key = await graph.Authorization.Users[NameRules.McpUserName].CreateKeyAsync(administration, CancellationToken.None);
        var requiredAfterCreate = access.RequiresKey;
        await graph.Authorization.Users[NameRules.McpUserName].RevokeKeyAsync(administration, CancellationToken.None);

        // Assert
        Assert.StartsWith("hb_", key.Secret);
        Assert.DoesNotContain(key.Secret, key.ToString(), StringComparison.Ordinal);
        Assert.True(requiredAfterCreate);
        Assert.False(access.RequiresKey);
    }

    [Theory]
    [InlineData(AdminSeeding.AdminUserName)]
    [InlineData(NameRules.AnonymousUserName)]
    public async Task WhenTheKeyOperationsAreCalledOnAnotherUser_ThenTheyAreRefused(string userName)
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var administration = graph.AdministrationAs(graph.AdminUserId);
        var user = graph.Authorization.Users[userName];

        // Act & Assert
        var create = await Assert.ThrowsAsync<InvalidOperationException>(() => user.CreateKeyAsync(administration, CancellationToken.None));
        var revoke = await Assert.ThrowsAsync<InvalidOperationException>(() => user.RevokeKeyAsync(administration, CancellationToken.None));
        Assert.Equal("Only the built-in user 'mcp' has a key.", create.Message);
        Assert.Equal("Only the built-in user 'mcp' has a key.", revoke.Message);
        Assert.False(((IMcpClientAccess)graph.UserStore).RequiresKey);
    }
}
