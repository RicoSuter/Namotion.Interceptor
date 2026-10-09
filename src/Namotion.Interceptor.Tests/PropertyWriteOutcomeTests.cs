using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

/// <summary>Model whose hooks can rewrite an incoming value to the current one or throw after assignment.</summary>
[InterceptorSubject]
public partial class WriteOutcomeSubject
{
    public partial string? Value { get; set; }

    public partial string? Other { get; set; }

    public bool RewriteToCurrentValue { get; set; }

    public bool ThrowAfterAssignment { get; set; }

    partial void OnValueChanging(ref string? newValue, ref bool cancel)
    {
        if (RewriteToCurrentValue)
        {
            newValue = Value;
        }
    }

    partial void OnValueChanged(string? newValue)
    {
        if (ThrowAfterAssignment)
        {
            throw new InvalidOperationException("Hook failed after assignment.");
        }
    }
}

public class PropertyWriteOutcomeTests
{
    /// <summary>Stops a write whose incoming value already equals the current one, like the tracking equality handler.</summary>
    private sealed class EqualityStopInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
            {
                next(ref context);
            }
        }
    }

    /// <summary>Vetoes the write of "new" and leaves the context looking like an equality stop.</summary>
    private sealed class RewritingVetoInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Equals(context.NewValue, "new"))
            {
                context.NewValue = context.CurrentValue;
                return;
            }

            next(ref context);
        }
    }

    private static WriteOutcomeSubject CreateSubject(IWriteInterceptor? interceptor = null)
    {
        var context = InterceptorSubjectContext.Create();
        if (interceptor is not null)
        {
            context.AddService(interceptor);
        }

        return new WriteOutcomeSubject(context) { Value = "old", Other = "old" };
    }

    private static PropertyReference ValueProperty(WriteOutcomeSubject subject) => new(subject, nameof(WriteOutcomeSubject.Value));

    [Fact]
    public void WhenInterceptorRewritesNewValueToCurrentAndVetoes_ThenOutcomeIsNeitherAcceptedNorMutated()
    {
        // Arrange
        var subject = CreateSubject(new RewritingVetoInterceptor());
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            subject.Value = "new";
        }

        // Assert
        Assert.Equal("old", subject.Value);
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenWriteReachesTerminal_ThenOutcomeIsAcceptedAndMutated()
    {
        // Arrange
        var subject = CreateSubject();
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            subject.Value = "new";
        }

        // Assert
        Assert.Equal("new", subject.Value);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenIncomingValueEqualsCurrentAndChainStops_ThenOutcomeIsAcceptedButNotMutated()
    {
        // Arrange
        var subject = CreateSubject(new EqualityStopInterceptor());
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "old"))
        {
            subject.Value = "old";
        }

        // Assert
        Assert.True(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenHookRewritesIncomingValueToCurrentAndChainStops_ThenOutcomeIsAcceptedButNotMutated()
    {
        // Arrange
        var subject = CreateSubject(new EqualityStopInterceptor());
        subject.RewriteToCurrentValue = true;
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            subject.Value = "new";
        }

        // Assert
        Assert.Equal("old", subject.Value);
        Assert.True(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenHookThrowsAfterAssignment_ThenOutcomeIsStillMutated()
    {
        // Arrange
        var subject = CreateSubject();
        subject.ThrowAfterAssignment = true;
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            Assert.Throws<InvalidOperationException>(() => subject.Value = "new");
        }

        // Assert
        Assert.Equal("new", subject.Value);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }

    [Fact]
    public void WhenDifferentPropertyIsWritten_ThenOutcomeStaysUntouchedUntilArmedPropertyIsWritten()
    {
        // Arrange
        var subject = CreateSubject();
        var outcome = new PropertyWriteOutcome();

        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            // Act
            subject.Other = "new";
            var acceptedAfterOtherWrite = outcome.Accepted;
            var mutatedAfterOtherWrite = outcome.Mutated;
            subject.Value = "new";

            // Assert
            Assert.False(acceptedAfterOtherWrite);
            Assert.False(mutatedAfterOtherWrite);
            Assert.True(outcome.Accepted);
            Assert.True(outcome.Mutated);
        }
    }

    [Fact]
    public void WhenArmedAgain_ThenPreviousResultIsCleared()
    {
        // Arrange
        var subject = CreateSubject();
        var outcome = new PropertyWriteOutcome();
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            subject.Value = "new";
        }

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "newer"))
        {
            // No write happens inside this scope.
        }

        // Assert
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenAnotherSubjectSharesThePropertyName_ThenItsWriteDoesNotReportIntoTheOutcome()
    {
        // Arrange
        var armedSubject = CreateSubject();
        var otherSubject = CreateSubject();
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(armedSubject), ChangeOrigin.Local, "new"))
        {
            otherSubject.Value = "new";
        }

        // Assert
        Assert.Equal("new", otherSubject.Value);
        Assert.Equal("old", armedSubject.Value);
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenScopeIsDisposed_ThenLaterWriteDoesNotReportIntoTheOutcome()
    {
        // Arrange
        var subject = CreateSubject();
        var outcome = new PropertyWriteOutcome();
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            // Leave the arming unconsumed so disposal is the only thing that can clear it.
        }

        // Act
        subject.Value = "new";

        // Assert
        Assert.Equal("new", subject.Value);
        Assert.False(outcome.Accepted);
        Assert.False(outcome.Mutated);
    }

    [Fact]
    public void WhenDefaultScopeIsDisposed_ThenArmedOutcomeStaysArmed()
    {
        // Arrange
        var subject = CreateSubject();
        var outcome = new PropertyWriteOutcome();

        // Act
        using (outcome.Arm(ValueProperty(subject), ChangeOrigin.Local, "new"))
        {
            default(PropertyWriteOutcomeScope).Dispose();
            subject.Value = "new";
        }

        // Assert
        Assert.True(outcome.Accepted);
        Assert.True(outcome.Mutated);
    }
}
