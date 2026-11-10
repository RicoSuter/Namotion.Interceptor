using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class PropertyTriggerStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    // An instant the seconds after the start, on the wall clock and on the monotonic clock alike.
    private static TriggerTime At(double seconds) => new(Start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);
    private static readonly TriggerObservation TrueValue = new(true, null, []);
    private static readonly TriggerObservation FalseValue = new(false, null, []);

    private static TriggerObservation Unknown(params string[] paths) => new(null, null, paths);

    private static TriggerObservation WithRearm(bool? value, bool? rearm) => new(value, rearm, []);

    // Observes the value and, when the trigger fires, runs its rule.
    private static bool Run(PropertyTriggerState state, TriggerObservation observation, TriggerTime time)
    {
        var fired = state.Observe(observation, time);
        if (fired)
        {
            state.Consume();
        }

        return fired;
    }

    [Fact]
    public void WhenTheRunOfAFiredTriggerDidNotRun_ThenTheTriggerStaysPendingWhileTrueAndIsArmedWithoutARunWhenFalse()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, WithRearm(false, null), At(0));
        state.Observe(WithRearm(true, null), At(0));

        // Act
        var fired = state.Observe(WithRearm(true, null), At(20));
        var stillTrue = state.Observe(WithRearm(true, null), At(30));
        var phaseWhileTrue = state.Phase;
        var pendingSince = state.PendingSince;
        var turnedFalse = state.Observe(WithRearm(false, null), At(40));

        // Assert
        Assert.True(fired);
        Assert.False(stillTrue);
        Assert.Equal(TriggerPhase.Pending, phaseWhileTrue);
        Assert.Equal(Start.AddSeconds(20), pendingSince);
        Assert.False(turnedFalse);
        Assert.Equal(TriggerPhase.Armed, state.Phase);
        Assert.Null(state.PendingSince);
    }

    [Fact]
    public void WhenThePendingRunRuns_ThenTheTriggerWaitsToRearm()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));
        state.Observe(TrueValue, At(0));

        // Act
        state.Consume();
        var stillTrue = state.Observe(TrueValue, At(1));

        // Assert
        Assert.False(stillTrue);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
        Assert.Null(state.PendingSince);
    }

    [Fact]
    public void WhenTheExpressionTurnsTrueWithoutHold_ThenTheTriggerFiresOnceUntilItIsFalseAgain()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));

        // Act
        var first = Run(state, TrueValue, At(0));
        var repeated = Run(state, TrueValue, At(1));
        var rearmed = Run(state, FalseValue, At(2));
        var second = Run(state, TrueValue, At(3));

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
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));

        // Act
        var started = Run(state, TrueValue, At(0));
        var phaseWhileHolding = state.Phase;
        var early = Run(state, TrueValue, At(19));
        var elapsed = Run(state, TrueValue, At(20));

        // Assert
        Assert.False(started);
        Assert.Equal(TriggerPhase.Holding, phaseWhileHolding);
        Assert.False(early);
        Assert.True(elapsed);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void WhenTheExpressionIsNotTrueDuringTheHold_ThenTheHoldStartsAgain(bool? interruption)
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));

        // Act
        Run(state, interruption is null ? Unknown("/Weather/Gust") : FalseValue, At(10));
        Run(state, TrueValue, At(15));
        var atOriginalHoldEnd = Run(state, TrueValue, At(20));
        var beforeHold = Run(state, TrueValue, At(34));
        var afterHold = Run(state, TrueValue, At(35));

        // Assert
        Assert.False(atOriginalHoldEnd);
        Assert.False(beforeHold);
        Assert.True(afterHold);
    }

    [Fact]
    public void WhenARearmExpressionIsSet_ThenOnlyItRearmsTheTrigger()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));

        // Act
        var first = Run(state, WithRearm(true, null), At(0));
        var belowThreshold = Run(state, WithRearm(false, false), At(1));
        var aboveAgain = Run(state, WithRearm(true, false), At(2));
        Run(state, WithRearm(false, true), At(3));
        var second = Run(state, WithRearm(true, false), At(4));

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
        var activated = state.Activate(TriggerActivation.EvaluateImmediately, Unknown("/Weather/Gust"), At(0));
        Run(state, Unknown("/Weather/Gust"), At(5));
        var unknownSince = state.UnknownSince;
        var missingPaths = state.MissingPaths.ToArray();
        var fired = Run(state, TrueValue, At(6));
        var isUnknownAfterTrue = state.IsUnknown;
        Run(state, Unknown("/Weather/Gust"), At(7));

        // Assert
        Assert.False(activated);
        Assert.Equal(Start, unknownSince);
        Assert.Equal(["/Weather/Gust"], missingPaths);
        Assert.True(fired);
        Assert.False(isUnknownAfterTrue);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
    }

    [Fact]
    public void WhenActivationIsImmediateAndTheExpressionIsTrue_ThenTheHoldStartsAtActivation()
    {
        // Arrange
        var withoutHold = new PropertyTriggerState(hold: null, hasRearm: false);
        var withHold = new PropertyTriggerState(Hold, hasRearm: false);

        // Act
        var firedAtOnce = withoutHold.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));
        var firedWithHold = withHold.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));

        // Assert
        Assert.True(firedAtOnce);
        Assert.False(firedWithHold);
        Assert.Equal(Start, withHold.HoldStartedAt);
    }

    [Fact]
    public void WhenTheWallClockMovesDuringAHold_ThenTheHoldEndsAfterItsDurationOnTheMonotonicClock()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));
        var shiftedForward = new TriggerTime(Start.AddHours(2), TimeSpan.FromSeconds(5));

        // Act
        var firedEarly = state.Observe(TrueValue, shiftedForward);
        var holdEnd = state.GetHoldEnd(shiftedForward);
        var firedAfterTheHold = state.Observe(TrueValue, new TriggerTime(Start.AddHours(-1), Hold));

        // Assert
        Assert.False(firedEarly);
        Assert.Equal(Start.AddHours(2).AddSeconds(15), holdEnd);
        Assert.True(firedAfterTheHold);
    }

    [Fact]
    public void WhenTheValueStaysUnknownWithTheSameMissingPaths_ThenTheVersionStays()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, Unknown("/Weather/Gust"), At(0));
        var versionAfterTheFirstObservation = state.Version;

        // Act
        state.Observe(Unknown("/Weather/Gust"), At(1));
        var versionAfterTheSamePaths = state.Version;
        state.Observe(Unknown("/Weather/Rain"), At(2));

        // Assert
        Assert.Equal(versionAfterTheFirstObservation, versionAfterTheSamePaths);
        Assert.NotEqual(versionAfterTheSamePaths, state.Version);
        Assert.Equal(["/Weather/Rain"], state.MissingPaths);
    }

    [Fact]
    public void WhenAHoldWouldEndAfterTheLatestInstant_ThenItsEndIsTheLatestInstant()
    {
        // Arrange
        var state = new PropertyTriggerState(TimeSpan.MaxValue, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));

        // Act
        var holdEnd = state.GetHoldEnd(At(1));

        // Assert
        Assert.Equal(DateTimeOffset.MaxValue, holdEnd);
    }

    [Fact]
    public void WhenActivationWaitsForFalseToTrue_ThenATrueBaselineMustTurnFalseFirst()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);

        // Act
        var activated = state.Activate(TriggerActivation.WaitForFalseToTrue, TrueValue, At(0));
        var stillTrue = Run(state, TrueValue, At(1));
        Run(state, FalseValue, At(2));
        var turnedTrue = Run(state, TrueValue, At(3));

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
        state.Activate(TriggerActivation.WaitForFalseToTrue, Unknown("/Weather/Gust"), At(0));

        // Act
        var firstKnown = Run(state, TrueValue, At(1));

        // Assert
        Assert.False(firstKnown);
        Assert.Equal(TriggerPhase.WaitingForFalse, state.Phase);
    }

    [Fact]
    public void WhenActivationWaitsForFalseToTrueWithRearm_ThenATrueBaselineWaitsForTheRearmExpression()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.WaitForFalseToTrue, WithRearm(true, null), At(0));

        // Act
        var movedBetween = Run(state, WithRearm(true, false), At(1));
        var falseButNotRearmed = Run(state, WithRearm(false, false), At(2));
        var trueAgain = Run(state, WithRearm(true, false), At(3));

        // Assert
        Assert.False(movedBetween);
        Assert.False(falseButNotRearmed);
        Assert.False(trueAgain);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenATriggerThatStartedTrueIsRearmedWithoutFiring_ThenItIsNoLongerTrueAtActivation(bool hasRearm)
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm);
        state.Activate(TriggerActivation.WaitForFalseToTrue, WithRearm(true, false), At(0));
        var wasTrueAtStart = state.WasTrueAtActivation;

        // Act
        Run(state, WithRearm(false, true), At(1));

        // Assert
        Assert.True(wasTrueAtStart);
        Assert.Equal(TriggerPhase.Armed, state.Phase);
        Assert.False(state.WasTrueAtActivation);
    }

    [Fact]
    public void WhenATriggerFiresWithoutStartingTrue_ThenItWasNotTrueAtActivation()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.WaitForFalseToTrue, WithRearm(false, null), At(0));

        // Act
        Run(state, WithRearm(true, false), At(1));

        // Assert
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
        Assert.False(state.WasTrueAtActivation);
    }

    [Fact]
    public void WhenATrueBaselineWithRearmIsRearmed_ThenTheNextTrueValueFires()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.WaitForFalseToTrue, WithRearm(true, null), At(0));
        Run(state, WithRearm(false, true), At(1));

        // Act
        var fired = Run(state, WithRearm(true, false), At(2));

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void WhenATrueBaselineWithRearmHasAnUnknownRearm_ThenItIsUnknownAndDoesNotRearm()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.WaitForFalseToTrue, new TriggerObservation(true, null, ["/Weather/Gust"]), At(0));

        // Act
        var fired = Run(state, new TriggerObservation(true, null, ["/Weather/Gust"]), At(1));

        // Assert
        Assert.False(fired);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
        Assert.True(state.IsUnknown);
        Assert.Equal(Start, state.UnknownSince);
        Assert.Equal(["/Weather/Gust"], state.MissingPaths);
    }

    [Fact]
    public void WhenTheBaselineIsUnknownAndBecomesTrueWithRearm_ThenItWaitsForTheRearmExpression()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.WaitForFalseToTrue, Unknown("/Weather/Gust"), At(0));

        // Act
        var firstKnown = Run(state, WithRearm(true, false), At(1));
        var falseButNotRearmed = Run(state, WithRearm(false, false), At(2));
        var trueAgain = Run(state, WithRearm(true, false), At(3));

        // Assert
        Assert.False(firstKnown);
        Assert.False(falseButNotRearmed);
        Assert.False(trueAgain);
        Assert.Equal(TriggerPhase.WaitingToRearm, state.Phase);
    }

    [Fact]
    public void WhenTheRearmExpressionIsTrueWhileTheExpressionStaysTrue_ThenOnlyANewChangeToTrueFires()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, WithRearm(false, null), At(0));
        Run(state, WithRearm(true, false), At(1));

        // Act
        var rearmedWhileTrue = Run(state, WithRearm(true, true), At(2));
        var stillTrue = Run(state, WithRearm(true, true), At(3));
        var turnedFalse = Run(state, WithRearm(false, true), At(4));
        var trueAgain = Run(state, WithRearm(true, false), At(5));

        // Assert
        Assert.False(rearmedWhileTrue);
        Assert.False(stillTrue);
        Assert.False(turnedFalse);
        Assert.True(trueAgain);
    }

    [Fact]
    public void WhenTheExpressionIsUnknownAfterARearmWhileTrue_ThenTheNextTrueValueFires()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, WithRearm(false, null), At(0));
        Run(state, WithRearm(true, false), At(1));
        Run(state, WithRearm(true, true), At(2));

        // Act
        var unknown = Run(state, Unknown("/Weather/Gust"), At(3));
        var trueAgain = Run(state, WithRearm(true, false), At(4));

        // Assert
        Assert.False(unknown);
        Assert.True(trueAgain);
    }

    [Fact]
    public void WhenTheTriggerIsDeactivated_ThenObservationsAreIgnored()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));

        // Act
        state.Deactivate();
        var fired = Run(state, TrueValue, At(1));

        // Assert
        Assert.False(fired);
        Assert.Equal(TriggerPhase.Inactive, state.Phase);
    }

    [Fact]
    public void WhenTheRearmExpressionIsUnknownWhileWaitingToRearm_ThenTheTriggerIsUnknownAndDoesNotRearm()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));
        Run(state, WithRearm(true, null), At(0));

        // Act
        var whileUnknown = Run(state, new TriggerObservation(true, null, ["/Weather/Threshold"]), At(1));
        var isUnknown = state.IsUnknown;
        var missingPaths = state.MissingPaths.ToArray();
        var phaseWhileUnknown = state.Phase;
        var unknownSince = state.UnknownSince;
        var rearmFired = Run(state, WithRearm(true, true), At(2));

        // Assert
        Assert.False(whileUnknown);
        Assert.True(isUnknown);
        Assert.Equal(["/Weather/Threshold"], missingPaths);
        Assert.Equal(TriggerPhase.WaitingToRearm, phaseWhileUnknown);
        Assert.Equal(Start.AddSeconds(1), unknownSince);
        Assert.False(rearmFired);
        Assert.False(state.IsUnknown);
        Assert.Equal(TriggerPhase.RearmedWhileTrue, state.Phase);
    }

    [Fact]
    public void WhenTheRearmExpressionIsKnownTrueAfterUnknown_ThenTheTriggerRearms()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));
        Run(state, WithRearm(true, null), At(0));
        Run(state, new TriggerObservation(true, null, ["/Weather/Threshold"]), At(1));

        // Act
        var firedOnRearm = Run(state, WithRearm(false, true), At(2));
        var fired = Run(state, WithRearm(true, false), At(3));

        // Assert
        Assert.False(firedOnRearm);
        Assert.False(state.IsUnknown);
        Assert.True(fired);
    }

    [Fact]
    public void WhenTheTriggerIsActivatedAgainWithoutDeactivation_ThenTheStateIsFresh()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, Unknown("/Weather/Gust"), At(0));

        // Act
        state.Activate(TriggerActivation.EvaluateImmediately, Unknown("/Weather/Rain"), At(5));

        // Assert
        Assert.Equal(Start.AddSeconds(5), state.UnknownSince);
        Assert.Equal(["/Weather/Rain"], state.MissingPaths);
        Assert.Equal(TriggerPhase.Armed, state.Phase);
    }

    [Fact]
    public void WhenTheTriggerIsActivatedAfterDeactivation_ThenItFiresAgain()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));
        Run(state, TrueValue, At(1));
        state.Deactivate();

        // Act
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(2));
        var fired = Run(state, TrueValue, At(3));

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void WhenTheHoldIsZero_ThenTheTriggerFiresImmediately()
    {
        // Arrange
        var state = new PropertyTriggerState(TimeSpan.Zero, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, FalseValue, At(0));

        // Act
        var fired = Run(state, TrueValue, At(1));

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void WhenTheTriggerWaitsForFalseAndTheValueIsUnknown_ThenItStaysWaitingAndIsUnknown()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: false);
        state.Activate(TriggerActivation.WaitForFalseToTrue, TrueValue, At(0));

        // Act
        var fired = Run(state, Unknown("/Weather/Gust"), At(1));
        var firedOnTrue = Run(state, TrueValue, At(2));

        // Assert
        Assert.False(fired);
        Assert.False(firedOnTrue);
        Assert.Equal(TriggerPhase.WaitingForFalse, state.Phase);
        Assert.False(state.IsUnknown);
    }

    [Fact]
    public void WhenTheWallClockIsSetBackDuringAHold_ThenTheStartMovesBackByTheTimeHeld()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));
        var version = state.Version;

        // Act
        var fired = state.Observe(TrueValue, new TriggerTime(Start.AddMinutes(-10).AddSeconds(5), TimeSpan.FromSeconds(5)));

        // Assert
        Assert.False(fired);
        Assert.Equal(TriggerPhase.Holding, state.Phase);
        Assert.Equal(Start.AddMinutes(-10), state.HoldStartedAt);
        Assert.NotEqual(version, state.Version);
    }

    [Fact]
    public void WhenTheWallClockIsSetForwardDuringAHold_ThenTheStartStays()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));

        // Act
        state.Observe(TrueValue, new TriggerTime(Start.AddMinutes(10).AddSeconds(5), TimeSpan.FromSeconds(5)));

        // Assert
        Assert.Equal(Start, state.HoldStartedAt);
    }

    [Fact]
    public void WhenAStatusIsCreatedFromAPropertyTrigger_ThenItCopiesTheState()
    {
        // Arrange
        var state = new PropertyTriggerState(Hold, hasRearm: false);
        state.Activate(TriggerActivation.EvaluateImmediately, TrueValue, At(0));

        // Act
        var status = TriggerStatus.FromProperty(state, Hold);

        // Assert
        Assert.Equal(TriggerPhase.Holding, status.Phase);
        Assert.Equal(Start, status.HoldStartedAt);
        Assert.Equal(Hold, status.Hold);
        Assert.Null(status.UnknownSince);
        Assert.Empty(status.MissingPaths);
        Assert.Null(status.Problem);
    }
}
