using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationStopTests
{
    [Fact]
    public async Task WhenAnOperationThatTimedOutIgnoresItsCancellation_ThenTheStopAbandonsItAndTheWorkerEnds()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("stall", On("Stall", When("/Weather/Gust > 8"), Invoke("/Blinds/Stall"), Set("/Blinds/Position", "5")))));
        host.Weather.Gust = 9;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsStalled);
        host.Time.Advance(ActionExecutor.Timeout);
        var outcomeAfterTheTimeout = host.Rule("stall").LastRunOutcome;

        // Act
        await host.StopAsync();

        // Assert
        Assert.Null(outcomeAfterTheTimeout);
        Assert.Equal(ServiceStatus.Stopped, host.Automation.Status);
        Assert.Equal(AutomationRunOutcome.Canceled, host.Rule("stall").LastRunOutcome);
        Assert.Equal(0, host.Blinds.Position);
        host.Blinds.EndStall();
    }

    [Fact]
    public async Task WhenAnOperationThrowsAnotherExceptionOnItsCancellationByAStop_ThenTheRunIsRecordedAsCanceled()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("call", On("Call", When("/Weather/Gust > 8"), Invoke("/Blinds/DisconnectOnCancel")))));
        host.Weather.Gust = 9;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsDisconnecting);

        // Act
        await host.StopAsync();

        // Assert
        Assert.Equal(AutomationRunOutcome.Canceled, host.Rule("call").LastRunOutcome);
        Assert.Null(host.Automation.StatusMessage);
        Assert.Equal(ServiceStatus.Stopped, host.Automation.Status);
    }

    [Fact]
    public async Task WhenTheWorkerDoesNotEndWithinTheStopTimeout_ThenTheStopReturnsAndTheWorkerChangesNothingAfterARestart()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Closed"];
            automation.InitialState = "Idle";
            automation.Rules = Keyed(
                ("block", On("Block", When("/Weather/Gust > 8"), Invoke("/Blinds/Block"), Set("/Blinds/Position", "5"), Enter("Closed"))),
                ("rain", On("Rain", When("/Weather/Rain > 0"), Invoke("/Blinds/Close"))));
        });
        host.Weather.Gust = 9;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsBlocked);
        var oldWorker = host.Automation.ExecuteTask!;

        // Act
        var stop = host.StopAsync();
        host.Time.Advance(AutomationRuntime.StopTimeout);
        await stop;
        host.Weather.Gust = 0;
        await ((IHostedService)host.Automation).StartAsync(CancellationToken.None);
        await host.FlushAsync();
        host.Weather.Rain = 1;
        await host.FlushAsync();
        var checkpoint = await File.ReadAllTextAsync(host.CheckpointPath);
        host.Blinds.Unblock();
        await oldWorker;

        // Assert
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Equal("Idle", host.Automation.CurrentState);
        Assert.Equal(0, host.Blinds.Position);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal("On: Armed", host.Rule("block").Status);
        Assert.Equal(1L, host.Rule("rain").RunCount);
        Assert.Equal(checkpoint, await File.ReadAllTextAsync(host.CheckpointPath));
    }

    [Fact]
    public async Task WhenTheStopIsCanceledBeforeTheWorkerEnded_ThenTheOldWorkerDoesNotStopTheRestartedAutomation()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("block", On("Block", When("/Weather/Gust > 8"), Invoke("/Blinds/Block"), Set("/Blinds/Position", "5")))));
        host.Weather.Gust = 9;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsBlocked);
        var oldWorker = host.Automation.ExecuteTask!;

        // Act
        await ((IHostedService)host.Automation).StopAsync(new CancellationToken(canceled: true));
        host.Weather.Gust = 0;
        await ((IHostedService)host.Automation).StartAsync(CancellationToken.None);
        await host.FlushAsync();
        host.Blinds.Unblock();
        await oldWorker;

        // Assert
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Equal(0, host.Blinds.Position);
    }

    [Fact]
    public async Task WhenAStopCancelsARun_ThenTheLaterRequestsOfTheSameInputDoNotRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.RuleOrder = ["wait", "close"];
            automation.Rules = Keyed(
                ("wait", On("Wait", When("/Weather/Gust > 8"), Invoke("/Blinds/Wait"))),
                ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
        });
        host.Weather.Gust = 9;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);

        // Act
        await host.StopAsync();

        // Assert
        Assert.Equal(AutomationRunOutcome.Canceled, host.Rule("wait").LastRunOutcome);
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal(0L, host.Rule("close").RunCount);
    }

    [Fact]
    public async Task WhenTheAutomationRunsAndThenStops_ThenItRunsItsRulesOnlyWhileRunning()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        var isRunningRulesWhileRunning = host.Automation.IsRunningRules;

        // Act
        await host.StopAsync();

        // Assert
        Assert.True(isRunningRulesWhileRunning);
        Assert.Equal(ServiceStatus.Stopped, host.Automation.Status);
        Assert.False(host.Automation.IsRunningRules);
    }

    [Fact]
    public async Task WhenTheWorkerEndsWithAnUnexpectedError_ThenTheAutomationShowsTheErrorAndRunsNoRules()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        var automation = new Automation(host.Graph.Resolver, new FirstTimerFailingClock(host.Clock), host.Authorizer, host.ServiceProvider, host.Logger)
        {
            Name = "Crashing"
        };
        host.Graph.Add("Crashing", automation);

        // Act
        await ((IHostedService)automation).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => automation.Status == ServiceStatus.Error);

        // Assert
        Assert.Equal(FirstTimerFailingClock.Message, automation.StatusMessage);
        Assert.False(automation.IsRunningRules);
        await ((IHostedService)automation).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenAnActionFails_ThenTheAutomationShowsTheErrorAndKeepsRunningItsRules()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("jam", On("Jam", When("/Weather/Gust > 8"), Invoke("/Blinds/Jam")))));

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, host.Automation.Status);
        Assert.True(host.Automation.IsRunningRules);
    }

    // Fails to create the first timer, the wake timer that the worker creates once it started the rules, which ends the worker.
    private sealed class FirstTimerFailingClock(TimeProvider clock) : TimeProvider
    {
        public const string Message = "The timer could not be created.";

        private int _hasFailed;

        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override long GetTimestamp() => clock.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Interlocked.Exchange(ref _hasFailed, 1) == 0
                ? throw new InvalidOperationException(Message)
                : clock.CreateTimer(callback, state, dueTime, period);
    }
}
