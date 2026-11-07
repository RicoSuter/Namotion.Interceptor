using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class AuthenticationEventsTests
{
    [Fact]
    public async Task WhenUsersAndTheMcpKeyChange_ThenEachChangeIsLoggedWithItsActorButNoSecret()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        var administration = host.AdministrationAsAdmin;

        // Act
        var added = await administration.AddUserAsync("alice", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        var locked = await administration.SetLockedAsync("alice", true, CancellationToken.None);
        var key = await administration.CreateMcpKeyAsync(CancellationToken.None);

        // Assert
        Assert.True(added.Succeeded, added.Error);
        Assert.True(locked.Succeeded, locked.Error);
        Assert.True(key.Succeeded, key.Error);
        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains($"The user alice was changed by admin: added with the roles {BuiltInRoles.ReaderName}.", messages);
        Assert.Contains("A setup code for alice was created by admin.", messages);
        Assert.Contains("The user alice was changed by admin: locked.", messages);
        Assert.Contains("The key of the MCP user was changed by admin: created.", messages);
        Assert.DoesNotContain(messages, message => message.Contains(added.SetupCode!, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains(key.McpKey!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenARoleIsSaved_ThenTheChangeIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        var roleStore = host.Services.GetRequiredService<RoleStore>();

        // Act
        var result = await roleStore.SaveRoleAsync(null, new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.OperatorName] }, null, CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("The role family was changed by unknown: created.", logger.Entries.Select(entry => entry.Message));
    }
}
