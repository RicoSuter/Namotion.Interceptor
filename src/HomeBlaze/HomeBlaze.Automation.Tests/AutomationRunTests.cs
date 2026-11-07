using System.Reactive;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationRunTests
{
    [Fact]
    public async Task WhenARuleIsADryRun_ThenTheRunIsRecordedAndNothingIsExecuted()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(("retract", Create(
                "Retract",
                AutomationRuleMode.DryRun,
                [When("/Weather/Gust > 8")],
                [Invoke("/Blinds/MoveTo", ("position", "/Weather/Gust * 2")), Enter("Night")])));
        });
        var rule = host.Rule("retract");

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1L, rule.RunCount);
        Assert.Equal(AutomationRunOutcome.DryRun, rule.LastRunOutcome);
        Assert.Equal("Would run /Blinds/MoveTo(position: 18); ChangeState(Night)", rule.LastRunMessage);
        Assert.Equal(0, host.Blinds.Position);
        Assert.Equal("Day", host.Automation.CurrentState);
        Assert.Null(rule.LastTriggeredAt);
        Assert.Equal("DryRun: Triggered, waiting to rearm", rule.Status);
    }

    [Theory]
    [InlineData(AutomationRuleMode.On, AutomationRunOutcome.CompletedWithErrors, "action 'Close' failed: /Blinds/Close: the authorization failed: The policy store is offline")]
    [InlineData(AutomationRuleMode.DryRun, AutomationRunOutcome.DryRun, "Would run /Blinds/Close: the authorization failed: The policy store is offline")]
    public async Task WhenTheAuthorizerThrows_ThenTheRunIsRecordedWithTheFailure(AutomationRuleMode mode, AutomationRunOutcome outcome, string message)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(
            automation => automation.Rules = Keyed(("retract", Create("Retract", mode, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")]))),
            authorizer: _ => new ThrowingAuthorizer());
        var rule = host.Rule("retract");

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1L, rule.RunCount);
        Assert.Equal(outcome, rule.LastRunOutcome);
        Assert.Equal(message, rule.LastRunMessage);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAnActionFails_ThenTheStatusShowsTheErrorUntilAFullySuccessfulRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("jam", On("Jam", When("/Weather/Gust > 8"), Invoke("/Blinds/Jam"))),
            ("close", On("Close", When("/Weather/Rain > 0"), Invoke("/Blinds/Close")))));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var statusAfterFailure = host.Automation.Status;
        var messageAfterFailure = host.Automation.StatusMessage;
        host.Weather.Rain = 1;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterFailure);
        Assert.Equal("Rule 'Jam': action 'Jam' failed: /Blinds/Jam() failed: The motor is jammed.", messageAfterFailure);
        Assert.Equal(AutomationRunOutcome.CompletedWithErrors, host.Rule("jam").LastRunOutcome);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenAStateChangeSubscriberFails_ThenTheStateIsEnteredAndTheRunSucceeds()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(("night", On("Night", When("/Weather/Gust > 8"), Enter("Night"))));
        });
        using var subscription = host.Automation.Subscribe(
            Observer.Create<AutomationStateChangedEvent>(_ => throw new InvalidOperationException("The subscriber failed.")));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal("Night", host.Automation.CurrentState);
        Assert.Equal(AutomationRunOutcome.Succeeded, host.Rule("night").LastRunOutcome);
        Assert.Equal("Ran ChangeState(Night)", host.Rule("night").LastRunMessage);
    }

    [Fact]
    public async Task WhenARuleIsOff_ThenItIgnoresItsTriggers()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", Create("Retract", AutomationRuleMode.Off, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")]))));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(0L, host.Rule("retract").RunCount);
        Assert.Equal("Off", host.Rule("retract").Status);
    }

    [Fact]
    public async Task WhenTheAutomationIsDisabled_ThenNoRuleRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.Enabled = false;
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, host.Automation.Status);
        Assert.Equal("On: Inactive: the automation is disabled", host.Rule("retract").Status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenRunsAreRecorded_ThenTheCheckpointKeepsThemAtAConstantSize()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await host.FlushAsync();
        await AdvanceAsync(host, TimeSpan.FromSeconds(5));
        var sizeAfterOneRun = new FileInfo(host.CheckpointPath).Length;

        // Act
        host.Weather.Gust = 5;
        host.Weather.Gust = 9;
        await host.FlushAsync();
        await AdvanceAsync(host, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(sizeAfterOneRun, new FileInfo(host.CheckpointPath).Length);
        Assert.Equal(2L, await ReadRunCountAsync(host, "retract"));
    }

    [Fact]
    public async Task WhenRulesRun_ThenTheirRunsAreWrittenAtMostEveryFiveSeconds()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await host.FlushAsync();
        host.Weather.Gust = 5;
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await AdvanceAsync(host, TimeSpan.FromSeconds(4));
        var writtenAfterFourSeconds = await ReadRunCountAsync(host, "retract");
        await AdvanceAsync(host, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(2L, host.Rule("retract").RunCount);
        Assert.Null(writtenAfterFourSeconds);
        Assert.Equal(2L, await ReadRunCountAsync(host, "retract"));
    }

    [Fact]
    public async Task WhenTheAutomationStops_ThenTheRunsNotWrittenYetAreWritten()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await host.StopAsync();

        // Assert
        Assert.Equal(1L, await ReadRunCountAsync(host, "retract"));
    }

    [Fact]
    public async Task WhenTheStateChanges_ThenTheRunsNotWrittenYetAreWrittenWithIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(
                ("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))),
                ("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")));
        });
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        host.Weather.Power = 600;
        await host.FlushAsync();

        // Assert
        using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(host.CheckpointPath));
        Assert.Equal("Running", checkpoint.RootElement.GetProperty("currentState").GetString());
        Assert.Equal(1L, await ReadRunCountAsync(host, "retract"));
    }

    [Fact]
    public async Task WhenTheRunsCannotBeWritten_ThenTheAutomationShowsTheError()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });
        File.Delete(host.CheckpointPath);
        Directory.CreateDirectory(host.CheckpointPath);
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var statusAfterTheRun = host.Automation.Status;

        // Act
        await AdvanceAsync(host, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(ServiceStatus.Running, statusAfterTheRun);
        Assert.Equal(1L, host.Rule("retract").RunCount);
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.StartsWith("The runs could not be saved and are saved again in 5 seconds", host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheRunsCannotBeWrittenRepeatedlyAndThenAreWritten_ThenTheErrorIsLoggedOnceAndCleared()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });
        File.Delete(host.CheckpointPath);
        Directory.CreateDirectory(host.CheckpointPath);
        host.Weather.Gust = 9;
        await host.FlushAsync();
        for (var retry = 0; retry < 4; retry++)
        {
            await AdvanceAsync(host, TimeSpan.FromSeconds(5));
        }

        var messageWhileFailing = host.Automation.StatusMessage;

        // Act
        Directory.Delete(host.CheckpointPath);
        await AdvanceAsync(host, TimeSpan.FromSeconds(5));

        // Assert
        Assert.StartsWith("The runs could not be saved", messageWhileFailing);
        Assert.Single(host.Logger.Messages, message => message.Contains("The runs could not be saved", StringComparison.Ordinal));
        Assert.True(File.Exists(host.CheckpointPath));
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenARequestDoesNotRunAfterAFailedRun_ThenTheErrorStaysShown()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("jam", On("Jam", When("/Weather/Rain > 0"), Invoke("/Blinds/Jam"))),
            ("close", Create("Close", AutomationRuleMode.On, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")], guard: "/Blinds/IsLocked"))));
        host.Weather.Rain = 1;
        await host.FlushAsync();
        var messageAfterTheFailure = host.Automation.StatusMessage;

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.NotNull(messageAfterTheFailure);
        Assert.Equal(messageAfterTheFailure, host.Automation.StatusMessage);
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.Equal(0L, host.Rule("close").RunCount);
    }

    private static async Task AdvanceAsync(AutomationTestHost host, TimeSpan time)
    {
        host.Time.Advance(time);
        await host.FlushAsync();
    }

    private static async Task<long?> ReadRunCountAsync(AutomationTestHost host, string ruleKey)
    {
        if (!File.Exists(host.CheckpointPath))
        {
            return null;
        }

        using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(host.CheckpointPath));
        return checkpoint.RootElement.TryGetProperty("rules", out var rules) && rules.TryGetProperty(ruleKey, out var rule)
            ? rule.GetProperty("runCount").GetInt64()
            : null;
    }

    [Fact]
    public async Task WhenTheAutomationStopsWithAFlushPending_ThenTheFlushCompletes()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait")))));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        var flush = host.FlushAsync();

        // Act
        await host.StopAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => flush.IsCompleted, timeout: TimeSpan.FromSeconds(5));
        Assert.True(flush.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenTwoFlushesAreQueuedTogether_ThenBothComplete()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("wait", On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait")))));
        host.Weather.Rain = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        var flushes = Task.WhenAll(host.FlushAsync(), host.FlushAsync());

        // Act
        host.Blinds.Release();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => flushes.IsCompleted, timeout: TimeSpan.FromSeconds(5));
        Assert.True(flushes.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenTheRolesChangeWithoutBeingApplied_ThenRunsKeepTheAppliedRoles()
    {
        // Arrange
        var authorizer = RecordingAuthorizer.AllowingAll();
        await using var host = await AutomationTestHost.StartAsync(
            automation =>
            {
                automation.Roles = ["Operator"];
                automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
            },
            authorizer: _ => authorizer);

        // Act
        host.Automation.Roles = ["Reader"];
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var beforeApply = authorizer.Requests[^1].ToArray();
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        host.Weather.Gust = 0;
        await host.FlushAsync();
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(["Operator"], beforeApply);
        Assert.Equal(["Reader"], authorizer.Requests[^1].ToArray());
        Assert.Equal(2, host.Blinds.CloseCount);
    }
}
