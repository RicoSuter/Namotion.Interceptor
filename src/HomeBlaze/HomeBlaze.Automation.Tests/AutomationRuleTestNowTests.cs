using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Tests.Testing;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationRuleTestNowTests
{
    private static AutomationRule Retract(string onlyIf = "not /Blinds/IsMoving", AutomationRuleMode mode = AutomationRuleMode.On) => Create(
        "Retract",
        mode,
        [When("/Weather/Gust > 8", TimeSpan.FromSeconds(20)), OnEvent("/Button", "TestPressEvent", "event/Gesture == Release")],
        [Invoke("/Blinds/MoveTo", ("position", "/Weather/Gust * 2")), Invoke("/Notifier/SendNotification", ("notification.Message", "Gust {{ /Weather/Gust | raw }}"))],
        onlyIf);

    [Fact]
    public async Task WhenARuleIsTested_ThenItShowsTriggersOnlyIfAndRenderedActionsAndChangesNothing()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));
        host.Weather.Gust = 9;
        host.Blinds.IsMoving = true;
        await host.FlushAsync();
        var rule = host.Rule("retract");
        var runsBefore = rule.RunCount;

        // Act
        var result = rule.TestNow();

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.IsActive);
        Assert.True(result.Triggers[0].Result);
        Assert.Equal("Fires when true for 00:00:20", result.Triggers[0].Note);
        Assert.Null(result.Triggers[1].Result);
        Assert.Equal("Runs on each TestPressEvent of /Button that passes the filter", result.Triggers[1].Note);
        Assert.True(result.HasOnlyIf);
        Assert.False(result.OnlyIfResult);
        Assert.Equal(["/Blinds/MoveTo(position: 18)", "/Notifier/SendNotification(notification.Message: \"Gust 9\")"], result.Actions.ToArray());
        Assert.Equal(0, host.Blinds.Position);
        Assert.Empty(host.Notifier.Sent);
        Assert.Equal(runsBefore, rule.RunCount);
        Assert.Null(rule.LastTriggeredAt);
    }

    [Fact]
    public async Task WhenValuesAreMissing_ThenTheTestNamesThePaths()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract(onlyIf: "/Weather/Rain > 0"))));

        // Act
        var result = host.Rule("retract").TestNow();

        // Assert
        Assert.Null(result.Triggers[0].Result);
        Assert.Equal(["/Weather/Gust"], result.Triggers[0].MissingPaths.ToArray());
        Assert.Null(result.OnlyIfResult);
        Assert.Equal(["/Weather/Rain"], result.OnlyIfMissingPaths.ToArray());
    }

    [Fact]
    public async Task WhenTheRuleIsOff_ThenItIsInactiveAndStillEvaluated()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract(mode: AutomationRuleMode.Off))));
        host.Weather.Gust = 9;

        // Act
        var result = host.Rule("retract").TestNow();

        // Assert
        Assert.False(result.IsActive);
        Assert.True(result.Triggers[0].Result);
    }

    [Fact]
    public async Task WhenTheAuthorizerThrows_ThenTheTestShowsTheFailureInsteadOfThrowing()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(
            automation => automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))),
            authorizer: new ThrowingAuthorizer());

        // Act
        var result = host.Rule("retract").TestNow();

        // Assert
        Assert.Null(result.Error);
        Assert.Equal(["/Blinds/Close: the authorization failed: The policy store is offline"], result.Actions.ToArray());
    }

    [Fact]
    public async Task WhenTheRuleIsInvalid_ThenTheTestReturnsTheError()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));
        var rule = host.Rule("retract");
        rule.Triggers = [When("/Weather/Gust >")];

        // Act
        var result = rule.TestNow();

        // Assert
        Assert.StartsWith("Rule 'Retract': trigger 1: ", result.Error);
        Assert.Empty(result.Triggers);
    }

    [Fact]
    public async Task WhenTestNowIsInvokedThroughItsMetadata_ThenItIsAQueryThatReturnsTheResult()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("retract", Retract())));
        var method = host.Rule("retract").TryGetRegisteredProperty(nameof(AutomationRule.TestNow))!.TryGetMethodMetadata()!;

        // Act
        var result = await method.InvokeAsync([], serviceProvider: null, CancellationToken.None);

        // Assert
        Assert.Equal(MethodKind.Query, method.Kind);
        Assert.IsType<AutomationRuleTestResult>(result);
    }
}
