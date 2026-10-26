using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class AuthorizationMcpAccessPolicyTests
{
    public static TheoryData<string, string> ActionNames => new()
    {
        { "read", "read" },
        { "write", "write" },
        { "execute", "execute" },
        { "history", "history" },
        { "READ", "read" },
        { "Write", "write" },
        { "eXecute", "execute" },
        { "HISTORY", "history" }
    };

    [Theory]
    [MemberData(nameof(ActionNames))]
    public void WhenARoleAllowsOnlyOneAction_ThenOnlyThatActionIsAllowedIgnoringCase(string action, string allowedAction)
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "OnlyOne",
            Allow = [new PermissionRule { Paths = ["/**"], Actions = [new PermissionAction(allowedAction)] }]
        };
        var accessPolicy = TestAccessPolicies.Create(role.Name, role);
        var member = GetStateMember();

        // Act
        var isAllowed = accessPolicy.IsAllowed(member, action);
        var otherActions = AuthorizationActions.All
            .Where(other => !other.Name.Equals(allowedAction, StringComparison.OrdinalIgnoreCase))
            .Select(other => accessPolicy.IsAllowed(member, other.Name));

        // Assert
        Assert.True(isAllowed);
        Assert.All(otherActions, Assert.False);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("")]
    [InlineData("read ")]
    public void WhenTheActionIsUnknown_ThenItIsDenied(string action)
    {
        // Arrange
        var accessPolicy = TestAccessPolicies.Everything;
        var member = GetStateMember();

        // Act
        var isAllowed = accessPolicy.IsAllowed(member, action);

        // Assert
        Assert.False(isAllowed);
        Assert.True(accessPolicy.IsAllowed(member, McpAccessActions.Read));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("execute")]
    [InlineData("history")]
    [InlineData("delete")]
    public void WhenThePolicyIsDenyAll_ThenEveryActionIsDenied(string action)
    {
        // Arrange
        var accessPolicy = new AuthorizationMcpAccessPolicy(AuthorizationPolicy.DenyAll);

        // Act
        var isAllowed = accessPolicy.IsAllowed(GetStateMember(), action);

        // Assert
        Assert.False(isAllowed);
    }

    [Fact]
    public void WhenCreated_ThenThePolicyIsExposed()
    {
        // Act
        var accessPolicy = new AuthorizationMcpAccessPolicy(AuthorizationPolicy.DenyAll);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, accessPolicy.Policy);
    }

    [Fact]
    public void WhenAPolicyWithALoggerDeniesAnAction_ThenOnlyDeniedExecuteAndHistoryAreLoggedWithTheClientAndThePath()
    {
        // Arrange
        var logger = new RecordingLogger();
        var accessPolicy = new AuthorizationMcpAccessPolicy(AuthorizationPolicy.DenyAll, logger, "Watcher");
        var member = GetStateMember();

        // Act
        foreach (var action in AuthorizationActions.All)
        {
            accessPolicy.IsAllowed(member, action.Name);
        }

        // Assert
        Assert.Collection(
            logger.Entries,
            entry => AssertDenial(entry, "execute"),
            entry => AssertDenial(entry, "history"));

        static void AssertDenial((LogLevel Level, string Message, Exception? Exception) entry, string action)
        {
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Contains("Watcher", entry.Message);
            Assert.Contains(action, entry.Message);
            Assert.Contains("/Temperature", entry.Message);
        }
    }

    [Fact]
    public void WhenAPolicyWithALoggerAllowsAnAction_ThenNothingIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var accessPolicy = new AuthorizationMcpAccessPolicy(TestAccessPolicies.Everything.Policy, logger, "Watcher");

        // Act
        var isAllowed = accessPolicy.IsAllowed(GetStateMember(), AuthorizationActions.History.Name);

        // Assert
        Assert.True(isAllowed);
        Assert.Empty(logger.Entries);
    }

    private static RegisteredSubjectProperty GetStateMember()
    {
        var (room, _) = HomeBlazeMcpToolProviderTests.CreateTestSetup(isReadOnly: false);
        return room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestThing.Temperature))!;
    }
}
