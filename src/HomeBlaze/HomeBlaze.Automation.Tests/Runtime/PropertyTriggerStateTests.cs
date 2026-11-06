using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class PropertyTriggerStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);
    private static readonly TriggerObservation TrueValue = new(true, null, []);
    private static readonly TriggerObservation FalseValue = new(false, null, []);

    private static TriggerObservation Unknown(params string[] paths) => new(null, null, paths);

    private static TriggerObservation WithRearm(bool? value, bool? rearm) => new(value, rearm, []);

    [Fact]
    public void WhenTheExpressionTurnsTrueWithoutHold_ThenTheTriggerFiresOnceUntilItIsFalseAgain()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, Start);

        // Act
        var first = state.Observe(TrueValue, Start);
        var repeated = state.Observe(TrueValue, Start.AddSeconds(1));
        var rearmed = state.Observe(FalseValue, Start.AddSeconds(2));
        var second = state.Observe(TrueValue, Start.AddSeconds(3));

        // Assert
        Assert.True(first);
        Assert.False(repeated);
        Assert.False(rearmed);
        Assert.True(second);
    }

    [Fact]
    public void WhenAHoldIsSet_ThenTheTriggerFiresWhenTheHoldElapsed()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, Start);

        // Act
        var started = state.Observe(TrueValue, Start);
        var phaseWhileHolding = state.Phase;
        var early = state.Observe(TrueValue, Start.AddSeconds(19));
        var elapsed = state.Observe(TrueValue, Start.AddSeconds(20));

        // Assert
        Assert.False(started);
        Assert.Equal(TriggerPhase.Holding, phaseWhileHolding);
        Assert.False(early);
        Assert.True(elapsed);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
    }

    [Fact]
    public void WhenTheExpressionIsFalseOrUnknownDuringTheHold_ThenTheHoldStartsAgain()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, Start);

        // Act
        state.Observe(FalseValue, Start.AddSeconds(10));
        state.Observe(TrueValue, Start.AddSeconds(15));
        state.Observe(Unknown("/Weather/Gust"), Start.AddSeconds(16));
        state.Observe(TrueValue, Start.AddSeconds(30));
        var beforeHold = state.Observe(TrueValue, Start.AddSeconds(49));
        var afterHold = state.Observe(TrueValue, Start.AddSeconds(50));

        // Assert
        Assert.False(beforeHold);
        Assert.True(afterHold);
    }

    [Fact]
    public void WhenARearmExpressionIsSet_ThenOnlyItRearmsTheTrigger()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, Start);

        // Act
        var first = state.Observe(WithRearm(true, null), Start);
        var belowThreshold = state.Observe(WithRearm(false, false), Start.AddSeconds(1));
        var aboveAgain = state.Observe(WithRearm(true, false), Start.AddSeconds(2));
        state.Observe(WithRearm(false, true), Start.AddSeconds(3));
        var second = state.Observe(WithRearm(true, false), Start.AddSeconds(4));

        // Assert
        Assert.True(first);
        Assert.False(belowThreshold);
        Assert.False(aboveAgain);
        Assert.True(second);
    }

    [Fact]
    public void WhenTheValueIsUnknown_ThenNothingFiresOrRearmsAndTheStartOfTheUnknownIsKept()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);

        // Act
        var activated = state.Activate(TriggerActivation.EvaluateImmediately, Unknown("/Weather/Gust"), Start);
        state.Observe(Unknown("/Weather/Gust"), Start.AddSeconds(5));
        var unknownSince = state.UnknownSince;
        var missingPaths = state.MissingPaths.ToArray();
        var fired = state.Observe(TrueValue, Start.AddSeconds(6));
        var knownAgain = state.IsUnknown;
        state.Observe(Unknown("/Weather/Gust"), Start.AddSeconds(7));

        // Assert
        Assert.False(activated);
        Assert.Equal(Start, unknownSince);
        Assert.Equal(["/Weather/Gust"], missingPaths);
        Assert.True(fired);
        Assert.False(knownAgain);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
    }

    [Fact]
    public void WhenActivationIsImmediateAndTheExpressionIsTrue_ThenTheHoldStartsAtActivation()
    {
        // Arrange
        var withoutHold = new PropertyTriggerState(hold: null, hasRearm: false);
        var withHold = new PropertyTriggerState(Hold, hasRearm: false);

        // Act
        var firedAtOnce = withoutHold.Activate(TriggerActivation.EvaluateImmediately, TrueValue, Start);
        var firedWithHold = withHold.Activate(TriggerActivation.EvaluateImmediately, TrueValue, Start);

        // Assert
        Assert.True(firedAtOnce);
        Assert.False(firedWithHold);
        Assert.Equal(Start, withHold.HoldStartedAt);
    }

    [Fact]
    public void WhenActivationWaitsForFalseToTrue_ThenATrueBaselineMustTurnFalseFirst()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);

        // Act
        var activated = state.Activate(TriggerActivation.WaitForFalseToTrue, TrueValue, Start);
        var stillTrue = state.Observe(TrueValue, Start.AddSeconds(1));
        state.Observe(FalseValue, Start.AddSeconds(2));
        var turnedTrue = state.Observe(TrueValue, Start.AddSeconds(3));

        // Assert
        Assert.False(activated);
        Assert.False(stillTrue);
        Assert.True(turnedTrue);
    }

    [Fact]
    public void WhenActivationWaitsForFalseToTrueAndTheBaselineIsUnknown_ThenTheFirstKnownValueIsTheBaseline()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.WaitForFalseToTrue, Unknown("/Weather/Gust"), Start);

        // Act
        var firstKnown = state.Observe(TrueValue, Start.AddSeconds(1));

        // Assert
        Assert.False(firstKnown);
        Assert.Equal(TriggerPhase.WaitingForFalse, state.Phase);
    }

    [Fact]
    public void WhenTheTriggerIsDeactivated_ThenObservationsAreIgnored()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, Start);

        // Act
        state.Deactivate();
        var fired = state.Observe(TrueValue, Start.AddSeconds(1));

        // Assert
        Assert.False(fired);
        Assert.Equal(TriggerPhase.Inactive, state.Phase);
    }
}
