using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Tests.Runtime;

public class RuleRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly RuleGraph _graph = new();
    private readonly FakeTimeProvider _time = new(Now);

    private RuleRunner CreateRunner()
    {
        var executor = new ActionExecutor(_graph.Resolver, new AllowAllAutomationAuthorizer(), serviceProvider: null, _time);
        return new RuleRunner(executor, _graph.Resolver, _time, NullLogger.Instance);
    }

    private RunRequest Request(AutomationRule rule, CapturedEvent? capturedEvent = null)
    {
        _graph.Host.Rules = new Dictionary<string, AutomationRule> { ["r1"] = rule };
        return new RunRequest(RuleBuilder.Compile("r1", rule, "Idle", "Running"), capturedEvent, "trigger 1");
    }

    private static AutomationRule Rule(AutomationRuleMode mode, string? onlyIf, params AutomationAction[] actions) => new()
    {
        Name = "Storm",
        Mode = mode,
        OnlyIf = onlyIf,
        Triggers = [new PropertyTrigger { Expression = "/Weather/Gust > 8" }],
        Actions = actions
    };

    private static InvokeOperationAction Invoke(string path, ActionErrorPolicy onError = ActionErrorPolicy.Continue, string? position = null) => new()
    {
        Id = path[(path.LastIndexOf('/') + 1)..],
        OperationPath = path,
        OnError = onError,
        Arguments = position is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["position"] = position }
    };

    [Fact]
    public async Task WhenOnlyIfPasses_ThenTheActionsRunInOrderAndTheTriggerTimeIsSet()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, "not /Blinds/IsMoving", Invoke("/Blinds/MoveTo", position: "40"), Invoke("/Blinds/Close"));
        var host = new FakeRunHost(_graph.Host);

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), host, CancellationToken.None);

        // Assert
        Assert.Equal(AutomationRunOutcome.Succeeded, result.Outcome);
        Assert.Equal("Ran /Blinds/MoveTo(position: 40); /Blinds/Close()", result.Message);
        Assert.True(result.ExecutedActions);
        Assert.Null(result.Error);
        Assert.Equal(40, _graph.Blinds.Position);
        Assert.Equal(1, _graph.Blinds.CloseCount);
        Assert.Equal(Now, rule.LastTriggeredAt);
    }

    [Fact]
    public async Task WhenOnlyIfIsFalse_ThenTheRequestDoesNotRunAndNothingRuns()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, "not /Blinds/IsMoving", Invoke("/Blinds/Close"));
        _graph.Blinds.IsMoving = true;

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), new FakeRunHost(_graph.Host), CancellationToken.None);

        // Assert
        Assert.Null(result.Outcome);
        Assert.Equal("Only if is false", result.Message);
        Assert.False(result.ExecutedActions);
        Assert.Equal(0, _graph.Blinds.CloseCount);
        Assert.Null(rule.LastTriggeredAt);
    }

    [Fact]
    public async Task WhenOnlyIfIsUnknown_ThenTheRequestDoesNotRunNamingTheMissingPaths()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, "/Weather/Rain > 0", Invoke("/Blinds/Close"));

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), new FakeRunHost(_graph.Host), CancellationToken.None);

        // Assert
        Assert.Null(result.Outcome);
        Assert.Equal("Only if is unknown, missing /Weather/Rain", result.Message);
    }

    [Fact]
    public async Task WhenTheRuleIsADryRun_ThenNothingRunsAndTheMessageShowsTheRenderedActions()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.DryRun, null, Invoke("/Blinds/MoveTo", position: "/Weather/Gust * 2"), new ChangeStateAction { TargetState = "Running" });
        var host = new FakeRunHost(_graph.Host);

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), host, CancellationToken.None);

        // Assert
        Assert.Equal(AutomationRunOutcome.DryRun, result.Outcome);
        Assert.Equal("Would run /Blinds/MoveTo(position: 18); ChangeState(Running)", result.Message);
        Assert.Equal(0, _graph.Blinds.Position);
        Assert.Null(host.CurrentState);
        Assert.Null(rule.LastTriggeredAt);
    }

    [Fact]
    public async Task WhenAnActionFailsWithContinue_ThenTheRemainingActionsRun()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, null, Invoke("/Blinds/Jam"), Invoke("/Blinds/Close"));

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), new FakeRunHost(_graph.Host), CancellationToken.None);

        // Assert
        Assert.Equal(AutomationRunOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal("action 'Jam' failed: /Blinds/Jam() failed: The motor is jammed.", result.Message);
        Assert.Equal("Rule 'Storm': action 'Jam' failed: /Blinds/Jam() failed: The motor is jammed.", result.Error);
        Assert.Equal(1, _graph.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAnActionFailsWithStopRule_ThenTheRemainingActionsAreSkipped()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, null, Invoke("/Blinds/Jam", ActionErrorPolicy.StopRule), Invoke("/Blinds/Close"));

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), new FakeRunHost(_graph.Host), CancellationToken.None);

        // Assert
        Assert.Equal(AutomationRunOutcome.StoppedAfterError, result.Outcome);
        Assert.EndsWith("; the remaining actions were skipped", result.Message);
        Assert.Equal(0, _graph.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAnActionChangesTheState_ThenTheResultSaysSo()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, null, new ChangeStateAction { TargetState = "Running" });
        var host = new FakeRunHost(_graph.Host);

        // Act
        var result = await CreateRunner().RunAsync(Request(rule), host, CancellationToken.None);

        // Assert
        Assert.True(result.ChangedState);
        Assert.Equal("Ran ChangeState(Running)", result.Message);
        Assert.Equal("Running", host.CurrentState);
    }

    [Fact]
    public async Task WhenAnEventStartedTheRun_ThenTheArgumentsReadThePayload()
    {
        // Arrange
        var rule = new AutomationRule
        {
            Name = "Press",
            Mode = AutomationRuleMode.On,
            Triggers = [new SubjectEventTrigger { SubjectPath = "/Button", EventType = "TestPressEvent" }],
            Actions = [new SetPropertyAction { PropertyPath = "/Weather/Name", Value = "{{ event/Gesture }}" }]
        };
        var payload = new TestPressEvent { Timestamp = Now, DeviceId = "button", Gesture = "Release" };

        // Act
        await CreateRunner().RunAsync(Request(rule, new CapturedEvent(_graph.Blinds, payload)), new FakeRunHost(_graph.Host), CancellationToken.None);

        // Assert
        Assert.Equal("Release", _graph.Weather.Name);
    }

    [Fact]
    public async Task WhenTheModeIsUndefined_ThenNoActionRuns()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, null, Invoke("/Blinds/Close"));
        var request = Request(rule);
        rule.Mode = (AutomationRuleMode)5;
        var compiled = new CompiledRule("r1", rule, onlyIf: null);

        // Act
        var result = await CreateRunner().RunAsync(request with { Rule = compiled }, new FakeRunHost(_graph.Host), CancellationToken.None);

        // Assert
        Assert.Equal(0, _graph.Blinds.CloseCount);
        Assert.Null(rule.LastTriggeredAt);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task WhenTheAutomationStopsDuringARun_ThenTheRunIsCanceled()
    {
        // Arrange
        var rule = Rule(AutomationRuleMode.On, null, Invoke("/Blinds/Hang"));
        using var stopping = new CancellationTokenSource();
        var run = CreateRunner().RunAsync(Request(rule), new FakeRunHost(_graph.Host), stopping.Token);

        // Act
        await stopping.CancelAsync();
        var result = await run;

        // Assert
        Assert.Equal(AutomationRunOutcome.Canceled, result.Outcome);
        Assert.Equal("Canceled: the automation stopped", result.Message);
    }

    private sealed class FakeRunHost(IInterceptorSubject automation) : IRuleRunHost
    {
        public string Name => "Test";

        public IInterceptorSubject Automation => automation;

        public string? CurrentState { get; private set; }

        public IReadOnlyList<string> Roles { get; } = [];

        public IReadOnlyList<string>? RejectedRoles => null;

        public bool RunUnlessAbandoned(Action write)
        {
            write();
            return true;
        }

        public string? ChangeState(string targetState)
        {
            CurrentState = targetState;
            return null;
        }
    }
}
