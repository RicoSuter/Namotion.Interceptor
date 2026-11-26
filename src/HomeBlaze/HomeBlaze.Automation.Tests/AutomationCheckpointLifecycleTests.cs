using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Persistence;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationCheckpointLifecycleTests
{
    private static Action<Automation> Washer() => automation =>
    {
        automation.States = ["Idle", "Running"];
        automation.InitialState = "Idle";
        automation.Rules = Keyed(("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")));
    };

    private static async Task<string?> ReadStateAsync(string path)
    {
        using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return checkpoint.RootElement.GetProperty("currentState").GetString();
    }

    [Fact]
    public async Task WhenTheCheckpointCannotBeRead_ThenItIsMovedAsideAndTheErrorStaysUntilAReset()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            Washer()(automation);
            automation.Rules["close"] = On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"));
        });
        await host.StopAsync();
        await File.WriteAllTextAsync(host.CheckpointPath, "{ \"currentState\": ");

        // Act
        await ((IHostedService)host.Automation).StartAsync(CancellationToken.None);
        await host.FlushAsync();
        var stateAfterTheStart = host.Automation.CurrentState;
        var messageAfterTheStart = host.Automation.StatusMessage;
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var statusAfterARun = host.Automation.Status;
        var runs = host.Blinds.CloseCount;
        await host.ResetAsync();

        // Assert
        var setAside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(host.CheckpointPath)!, "Storm.json.unreadable-*"));
        Assert.Equal("{ \"currentState\": ", await File.ReadAllTextAsync(setAside));
        Assert.Equal("Idle", stateAfterTheStart);
        Assert.StartsWith("The checkpoint could not be read and was moved to " + setAside, messageAfterTheStart);
        Assert.Equal(1, runs);
        Assert.Equal(ServiceStatus.Error, statusAfterARun);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Null(host.Automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheCheckpointCannotBeReadAndAValidConfigurationIsSaved_ThenTheErrorIsCleared()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        await host.StopAsync();
        await File.WriteAllTextAsync(host.CheckpointPath, "not a checkpoint");
        await ((IHostedService)host.Automation).StartAsync(CancellationToken.None);
        await host.FlushAsync();
        var statusAfterTheStart = host.Automation.Status;

        // Act
        await host.SaveAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterTheStart);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
    }

    [Fact]
    public async Task WhenTheAutomationIsRemoved_ThenItsCheckpointIsDeleted()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        var existedBefore = File.Exists(host.CheckpointPath);

        // Act
        host.Graph.Remove("Storm");
        await host.StopAsync();

        // Assert
        Assert.True(existedBefore);
        Assert.False(File.Exists(host.CheckpointPath));
    }

    [Fact]
    public async Task WhenTheAutomationMovesToAnotherFolder_ThenItStartsFromItsInitialStateAtTheNewPath()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        host.Weather.Power = 600;
        await host.FlushAsync();
        host.Weather.Power = 0;
        var automation = host.Automation;
        var stateBeforeMove = automation.CurrentState;
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // Act
        // As storage moves a file: the same instance is added to its new folder before it is removed from the old one.
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Storm"] = automation };
        host.Graph.Remove("Storm");
        await automation.FlushAsync();
        var stateAfterMove = automation.CurrentState;
        host.Weather.Power = 600;
        await automation.FlushAsync();

        // Assert
        Assert.Equal("Running", stateBeforeMove);
        Assert.Equal("Idle", stateAfterMove);
        Assert.Equal(1L, host.Rule("started").RunCount);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Storm.json")));
        Assert.Equal("Running", await ReadStateAsync(Path.Combine(host.DataDirectory, "AutomationState", "Basement", "Storm.json")));
    }

    [Fact]
    public async Task WhenTheAutomationIsInBothFoldersWhileItMoves_ThenItsRunsAreNotTakenAlong()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        host.Weather.Power = 600;
        await host.FlushAsync();
        host.Weather.Power = 0;
        var automation = host.Automation;
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // Act
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Storm"] = automation };
        await automation.FlushAsync();
        host.Graph.Remove("Storm");
        await automation.FlushAsync();

        // Assert
        Assert.Null(host.Rule("started").LastTriggeredAt);
        Assert.Null(host.Rule("started").LastRunOutcome);
        Assert.Equal(0L, host.Rule("started").RunCount);
        Assert.Equal("Idle", automation.CurrentState);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Storm.json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Basement", "Storm.json")));
    }

    [Fact]
    public async Task WhenTheAutomationIsRemovedAndAddedAtAnotherPath_ThenItStartsFromItsInitialStateWithoutItsRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        host.Weather.Power = 600;
        await host.FlushAsync();
        host.Weather.Power = 0;
        var automation = host.Automation;
        var stateBeforeMove = automation.CurrentState;

        // Act
        host.Graph.Remove("Storm");
        await host.StopAsync();
        host.Graph.Add("Laundry", automation);
        await ((IHostedService)automation).StartAsync(CancellationToken.None);
        await automation.FlushAsync();

        // Assert
        Assert.Equal("Running", stateBeforeMove);
        Assert.Equal("Idle", automation.CurrentState);
        Assert.Null(host.Rule("started").LastTriggeredAt);
        Assert.Equal(0L, host.Rule("started").RunCount);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Storm.json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Laundry.json")));
    }

    [Fact]
    public async Task WhenTheAutomationHadAnErrorBeforeItMoved_ThenItRunsWithoutTheError()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("jam", On("Jam", When("/Weather/Rain > 0"), Invoke("/Blinds/Jam")))));
        host.Weather.Rain = 1;
        await host.FlushAsync();
        host.Weather.Rain = 0;
        var automation = host.Automation;
        var statusBeforeMove = automation.Status;
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // Act
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Storm"] = automation };
        host.Graph.Remove("Storm");
        await automation.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, statusBeforeMove);
        Assert.Equal(ServiceStatus.Running, automation.Status);
        Assert.Null(automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheAutomationStartedBeforeItHadAPath_ThenItLoadsTheCheckpointOfItsFirstPath()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => { });
        var checkpointPath = Path.Combine(host.DataDirectory, "AutomationState", "Basement", "Washer.json");
        new AutomationCheckpointStore(checkpointPath).Write(new AutomationCheckpoint
        {
            CurrentState = "Running",
            StateEnteredAt = AutomationTestHost.Start.AddHours(-1),
            Rules = new Dictionary<string, RuleCheckpoint> { ["started"] = new() { RunCount = 3, LastTriggeredAt = AutomationTestHost.Start.AddHours(-1) } }
        });
        var washer = host.CreateAutomation("Washer");
        Washer()(washer);

        // In the context of the graph as a root of its own, which has no path.
        ((IInterceptorSubject)washer).Context.AddFallbackContext(host.Graph.Context);
        await ((IHostedService)washer).StartAsync(CancellationToken.None);
        await washer.FlushAsync();
        var pathAtStart = host.Graph.Resolver.GetPath(washer, PathStyle.Canonical);
        var stateWithoutPath = washer.CurrentState;
        var basement = new TestRoot();
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Washer"] = washer };

        // Act
        host.Graph.Add("Basement", basement);
        await washer.FlushAsync();
        await ((IHostedService)washer).StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(pathAtStart);
        Assert.Equal("Idle", stateWithoutPath);
        Assert.Equal("Running", washer.CurrentState);
        Assert.Equal(3L, washer.Rules["started"].RunCount);
        Assert.Equal("Running", await ReadStateAsync(checkpointPath));
    }

    [Fact]
    public async Task WhenTheAutomationMovesBeforeItsWorkerWatchesTheGraph_ThenItFollowsTheMoveAtOnce()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => { });
        var washer = host.CreateAutomation("Washer");
        Washer()(washer);
        host.Graph.Add("Washer", washer);
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // The move happens on the worker while it registers, after the start took the path and before graph changes reach it.
        host.ChangeDispatcher.ListenerDecorator = listener =>
        {
            host.ChangeDispatcher.ListenerDecorator = null;
            basement.Children = new Dictionary<string, IInterceptorSubject> { ["Washer"] = washer };
            host.Graph.Remove("Washer");
            return listener;
        };

        // Act
        await ((IHostedService)washer).StartAsync(CancellationToken.None);
        await washer.FlushAsync();
        await ((IHostedService)washer).StopAsync(CancellationToken.None);

        // Assert
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Washer.json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "AutomationState", "Basement", "Washer.json")));
    }

    [Fact]
    public async Task WhenTheUnreadableCheckpointCannotBeMovedAside_ThenAValidConfigurationKeepsTheErrorUntilAReset()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        await host.StopAsync();
        var folder = Path.GetDirectoryName(host.CheckpointPath)!;
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(host.CheckpointPath, "{ \"currentState\": ");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await ((IHostedService)host.Automation).StartAsync(CancellationToken.None);
            await host.FlushAsync();
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Act
        await host.SaveAsync();
        var statusAfterTheSave = host.Automation.Status;
        var contentAfterTheSave = await File.ReadAllTextAsync(host.CheckpointPath);
        await host.ResetAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterTheSave);
        Assert.Equal("{ \"currentState\": ", contentAfterTheSave);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.NotEqual(contentAfterTheSave, await File.ReadAllTextAsync(host.CheckpointPath));
    }
}
