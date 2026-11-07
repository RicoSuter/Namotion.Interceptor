using HomeBlaze.AI.Mcp;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class AdminSeedingTests
{
    [Fact]
    public async Task WhenNoUserAndNoMarkerExist_ThenAdminIsSeededWithAnEmptyPasswordAndTheMarkerIsWritten()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateEmptyAsync();

        // Act
        var outcome = await graph.SeedAsync();

        // Assert
        Assert.Equal(SeedOutcome.Seeded, outcome);
        var admin = await graph.UserManager.FindByNameAsync("admin");
        Assert.NotNull(admin);
        Assert.True(admin.AllowsEmptyPassword);
        Assert.True(admin.MustChangePassword);
        Assert.Null(admin.PasswordHash);
        Assert.True(admin.LockoutEnabled);
        Assert.Contains(BuiltInRoles.EverythingName, await graph.UserManager.GetRolesAsync(admin));
        Assert.True(File.Exists(AdminSeeding.MarkerPath(graph.DatabasePath)));
        Assert.True(graph.Database.IsAvailable);
    }

    [Fact]
    public async Task WhenTheDatabaseIsSeeded_ThenTheBuiltInRolesAndUsersExist()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateEmptyAsync();

        // Act
        await graph.SeedAsync();

        // Assert
        Assert.Equal(
            BuiltInRoles.All.Select(role => role.Name).Order(StringComparer.Ordinal),
            (await graph.GetBuiltInRoleNamesAsync()).Order(StringComparer.Ordinal));

        var anonymous = await graph.UserManager.FindByNameAsync(NameRules.AnonymousUserName);
        var mcp = await graph.UserManager.FindByNameAsync(NameRules.McpUserName);
        Assert.NotNull(anonymous);
        Assert.NotNull(mcp);
        foreach (var user in new[] { anonymous, mcp })
        {
            Assert.True(user.IsBuiltIn);
            Assert.False(user.LockoutEnabled);
            Assert.Null(user.PasswordHash);
            Assert.False(user.AllowsEmptyPassword);
        }

        Assert.Empty(await graph.UserManager.GetRolesAsync(anonymous));
        Assert.Equal([BuiltInRoles.ReaderName], await graph.UserManager.GetRolesAsync(mcp));
    }

    [Fact]
    public async Task WhenAPersonAlreadyExists_ThenNothingIsSeededAndBuiltInUsersAreKept()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var mcpRolesChanged = await graph.AdministrationAs(graph.AdminUserId).SetRolesAsync(NameRules.McpUserName, [], CancellationToken.None);
        var adminId = graph.AdminUserId;
        File.Delete(AdminSeeding.MarkerPath(graph.DatabasePath));

        // Act
        var outcome = await graph.SeedAsync();

        // Assert
        Assert.True(mcpRolesChanged.Succeeded, mcpRolesChanged.Error);
        Assert.Equal(SeedOutcome.AlreadyHasUsers, outcome);
        Assert.Equal(adminId, graph.AdminUserId);
        Assert.Empty(graph.UserStore.McpRoles);
        Assert.True(File.Exists(AdminSeeding.MarkerPath(graph.DatabasePath)));
        Assert.True(graph.Database.IsAvailable);
    }

    [Fact]
    public async Task WhenTheMarkerExistsButNoUserDoes_ThenNothingIsSeededAndTheDatabaseIsUnavailable()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateEmptyAsync();
        await File.WriteAllTextAsync(AdminSeeding.MarkerPath(graph.DatabasePath), "seeded");

        // Act
        var outcome = await graph.SeedAsync();

        // Assert
        Assert.Equal(SeedOutcome.MarkerWithoutUsers, outcome);
        Assert.Null(await graph.UserManager.FindByNameAsync("admin"));
        Assert.False(graph.Database.IsAvailable);
        Assert.Equal(
            "The authentication database has no users although it was set up before. Run HomeBlaze once with --reset-admin.",
            graph.Database.UnavailableReason);
    }

    [Fact]
    public async Task WhenTheMarkerCannotBeWritten_ThenSeedingFailsRollsBackAndTheDatabaseIsUnavailable()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateEmptyAsync();
        var markerPath = AdminSeeding.MarkerPath(graph.DatabasePath);
        Directory.CreateDirectory(markerPath);
        try
        {
            // Act & Assert
            await Assert.ThrowsAnyAsync<Exception>(graph.SeedAsync);
            Assert.Null(await graph.UserManager.FindByNameAsync("admin"));
            Assert.Null(await graph.UserManager.FindByNameAsync(NameRules.McpUserName));
            Assert.False(graph.Database.IsAvailable);
            Assert.Equal(AdminSeeding.SeedingFailedReason, graph.Database.UnavailableReason);
        }
        finally
        {
            Directory.Delete(markerPath);
        }
    }

    [Fact]
    public async Task WhenTheDatabaseIsUnavailable_ThenNothingIsSeeded()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateEmptyAsync();
        graph.Database.MarkUnavailable("Something else went wrong.");

        // Act
        var outcome = await graph.SeedAsync();

        // Assert
        Assert.Equal(SeedOutcome.DatabaseUnavailable, outcome);
        Assert.Null(await graph.UserManager.FindByNameAsync("admin"));
        Assert.False(File.Exists(AdminSeeding.MarkerPath(graph.DatabasePath)));
        Assert.Equal("Something else went wrong.", graph.Database.UnavailableReason);
    }

    [Fact]
    public async Task WhenAdminWithLockoutDisabledIsReset_ThenItSucceedsAndLockoutIsEnabled()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var userManager = graph.UserManager;
        var admin = (await userManager.FindByNameAsync("admin"))!;
        Assert.True((await userManager.SetLockoutEnabledAsync(admin, false)).Succeeded);

        // Act
        await graph.ResetAdminAsync();

        // Assert
        var reset = (await graph.UserManager.FindByNameAsync("admin"))!;
        Assert.True(reset.LockoutEnabled);
        Assert.Null(reset.LockoutEnd);
        Assert.True(reset.AllowsEmptyPassword);
    }

    [Fact]
    public async Task WhenAdminIsReset_ThenEverySecurityStampChangesAndAdminAllowsAnEmptyPasswordAgain()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var momStamp = await graph.GetSecurityStampAsync((await graph.CreateUserAsync("mom")).Id);

        // Act
        await graph.ResetAdminAsync();

        // Assert
        var admin = (await graph.UserManager.FindByNameAsync("admin"))!;
        Assert.True(admin.AllowsEmptyPassword);
        Assert.Null(admin.PasswordHash);
        Assert.False(await graph.UserManager.IsLockedOutAsync(admin));
        Assert.NotEqual(momStamp, await graph.GetSecurityStampAsync(graph.UserStore.GetUserId("mom")!));
    }

    [Fact]
    public async Task WhenAnAdminWithAPasswordLockedOutWithoutEverythingIsReset_ThenItCanSignInWithAnEmptyPasswordAsAFullAdministrator()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        await graph.AddUserAsync("boss", [BuiltInRoles.EverythingName]);
        await graph.SetPasswordAsync("admin", "Secret-Password-1");
        var bossId = graph.UserStore.GetUserId("boss")!;
        var demoted = await graph.AdministrationAs(bossId).SetRolesAsync("admin", [BuiltInRoles.ReaderName], CancellationToken.None);
        var locked = await graph.AdministrationAs(bossId).SetLockedAsync("admin", true, CancellationToken.None);

        // Act
        await graph.ResetAdminAsync();

        // Assert
        Assert.True(demoted.Succeeded, demoted.Error);
        Assert.True(locked.Succeeded, locked.Error);
        var admin = (await graph.UserManager.FindByNameAsync("admin"))!;
        Assert.Null(admin.PasswordHash);
        Assert.True(admin.AllowsEmptyPassword);
        Assert.True(admin.MustChangePassword);
        Assert.False(await graph.UserManager.IsLockedOutAsync(admin));
        Assert.Contains(BuiltInRoles.EverythingName, graph.UserStore.GetRoles(admin.Id));
        Assert.False(graph.Authorization.Users["admin"].IsLockedOut);
    }

    [Fact]
    public async Task WhenAdminIsMissingAndIsReset_ThenItIsCreatedAndTheMarkerIsWritten()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateEmptyAsync();
        await File.WriteAllTextAsync(AdminSeeding.MarkerPath(graph.DatabasePath), "seeded");

        // Act
        await graph.ResetAdminAsync();
        var outcome = await graph.SeedAsync();

        // Assert
        var admin = await graph.UserManager.FindByNameAsync("admin");
        Assert.NotNull(admin);
        Assert.True(admin.AllowsEmptyPassword);
        Assert.Contains(BuiltInRoles.EverythingName, await graph.UserManager.GetRolesAsync(admin));
        Assert.Equal(SeedOutcome.AlreadyHasUsers, outcome);
        Assert.True(File.Exists(AdminSeeding.MarkerPath(graph.DatabasePath)));
    }

    [Fact]
    public async Task WhenTheAdminIsResetThroughTheServices_ThenTheDatabaseIsCreatedAndTheAdminSignsInWithAnEmptyPassword()
    {
        // Arrange
        using var database = new TestDatabase();
        await using var services = CreateAuthenticationServices(database.Path);

        // Act
        var isReset = await services.ResetAdminAsync();

        // Assert
        Assert.True(isReset);
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await userManager.FindByNameAsync(AdminSeeding.AdminUserName);
        Assert.NotNull(admin);
        Assert.True(admin.AllowsEmptyPassword);
        Assert.True(admin.MustChangePassword);
        Assert.Null(admin.PasswordHash);
        Assert.Contains(BuiltInRoles.EverythingName, await userManager.GetRolesAsync(admin));
        Assert.True(File.Exists(AdminSeeding.MarkerPath(database.Path)));
    }

    [Fact]
    public async Task WhenTheAdminIsResetThroughTheServicesAndTheDatabaseCannotBeOpened_ThenItReportsFailure()
    {
        // Arrange
        // A file in place of the folder of the database makes it impossible to create.
        var blockingFile = Path.GetTempFileName();
        try
        {
            await using var services = CreateAuthenticationServices(Path.Combine(blockingFile, "authentication.db"));

            // Act
            var isReset = await services.ResetAdminAsync();

            // Assert
            Assert.False(isReset);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    private static ServiceProvider CreateAuthenticationServices(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{AuthenticationOptions.SectionName}:{nameof(AuthenticationOptions.Database)}"] = databasePath })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddHomeBlazeServices();
        services.AddHomeBlazeAuthorization(new RulePathProvider());
        services.AddHomeBlazeAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WhenTheResetSwitchIsFollowedBySettings_ThenTheSettingsReachTheConfiguration()
    {
        // Arrange
        string[] args = ["--Reset-Admin", "--Authentication:Database", "/data/auth.db"];

        // Act
        var remaining = AuthenticationServiceProviderExtensions.RemoveResetAdminSwitch(args, out var isResetRequested);
        var configuration = new ConfigurationBuilder().AddCommandLine(remaining).Build();

        // Assert
        Assert.True(isResetRequested);
        Assert.Equal("/data/auth.db", configuration["Authentication:Database"]);
    }

    [Fact]
    public void WhenTheResetSwitchIsNotGiven_ThenTheArgumentsStayAsTheyAre()
    {
        // Act
        var remaining = AuthenticationServiceProviderExtensions.RemoveResetAdminSwitch(["--urls", "http://+:80"], out var isResetRequested);

        // Assert
        Assert.False(isResetRequested);
        Assert.Equal(["--urls", "http://+:80"], remaining);
    }
}
