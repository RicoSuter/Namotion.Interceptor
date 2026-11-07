using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationTargetsTests
{
    private readonly TestGraph _graph = new();
    private readonly TestBlinds _blinds = new();

    public AutomationTargetsTests() => _graph.Add("Blinds", _blinds);

    private RegisteredSubjectProperty Member(string name) => _blinds.TryGetRegisteredProperty(name)!;

    private static IAutomationPermissions AllowAll => new AllowAllAutomationAuthorizer().GetPermissions([]);

    [Theory]
    [InlineData("Close", true)]
    [InlineData("Calibrate", false)]
    [InlineData("GetPosition", false)]
    [InlineData("Position", false)]
    public void WhenEverythingIsAllowed_ThenOnlyOperationsWithoutConfirmationCanBeCalled(string member, bool expected)
    {
        // Act
        var canCall = AutomationTargets.CanCall(Member(member), AllowAll);

        // Assert
        Assert.Equal(expected, canCall);
    }

    [Theory]
    [InlineData("Position", true)]
    [InlineData("Level", false)]
    [InlineData("Label", false)]
    [InlineData("Close", false)]
    public void WhenEverythingIsAllowed_ThenOnlyWritableStateCanBeWritten(string member, bool expected)
    {
        // Act
        var canWrite = AutomationTargets.CanWrite(Member(member), AllowAll);

        // Assert
        Assert.Equal(expected, canWrite);
    }

    [Fact]
    public void WhenThePermissionsDeny_ThenNothingCanBeCalledOrWritten()
    {
        // Arrange
        var permissions = new DenyingPermissions();

        // Act
        var canCall = AutomationTargets.CanCall(Member("Close"), permissions);
        var canWrite = AutomationTargets.CanWrite(Member("Position"), permissions);

        // Assert
        Assert.False(canCall);
        Assert.False(canWrite);
    }

    [Fact]
    public void WhenTheAuthorizerThrows_ThenThePermissionsRefuseWithTheFailure()
    {
        // Act
        var permissions = AutomationTargets.GetPermissions(new ThrowingAuthorizer(), ["Operator"]);

        // Assert
        Assert.Equal("the authorization failed: The policy store is offline", permissions.Error);
        Assert.False(AutomationTargets.CanCall(Member("Close"), permissions));
    }

    [Fact]
    public void WhenTheRolesCannotBeUsed_ThenTheRolesErrorNamesThemOnlyWhileARuleCallsOrWrites()
    {
        // Arrange
        var permissions = new DenyingPermissions("the roles of the automation cannot be used: No roles are assigned.");
        AutomationRule[] calling = [On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))];
        AutomationRule[] stateOnly = [On("Night", When("/Weather/Gust > 8"), Enter("Night"))];

        // Act
        var callingError = AutomationTargets.GetRolesError(permissions, calling);
        var stateOnlyError = AutomationTargets.GetRolesError(permissions, stateOnly);
        var allowedError = AutomationTargets.GetRolesError(AllowAll, calling);

        // Assert
        Assert.Equal(
            "The actions that call operations or write properties are refused: the roles of the automation cannot be used: No roles are assigned.",
            callingError);
        Assert.Null(stateOnlyError);
        Assert.Null(allowedError);
    }
}
