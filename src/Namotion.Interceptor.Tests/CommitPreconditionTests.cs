using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class CommitPreconditionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenACommitLandsAfterTheRevisionWasObserved_ThenTheConditionalWriteIsSkipped(bool withWriteInterceptor)
    {
        // Arrange: both terminals are covered, since a context without write interceptors takes a
        // different one.
        var probe = new WriteOutcomeProbe();
        var context = InterceptorSubjectContext.Create();
        if (withWriteInterceptor)
        {
            context.WithService(() => probe);
        }

        var subject = new OriginProbeSubject(context) { Name = "observed" };
        var property = new PropertyReference(subject, nameof(OriginProbeSubject.Name));
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var observedRevision, out _));

        subject.Name = "newer";
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var newerRevision, out _));
        var timestampBefore = property.TryGetWriteTimestamp();
        probe.Reset();

        // Act
        var written = property.TrySetValueIfCommitRevisionIs("stale", observedRevision, includeSourceCommits: false);

        // Assert
        Assert.False(written);
        Assert.Equal("newer", subject.Name);
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var revisionAfter, out _));
        Assert.Equal(newerRevision, revisionAfter);
        Assert.Equal(timestampBefore, property.TryGetWriteTimestamp());
        if (withWriteInterceptor)
        {
            Assert.Equal((false, 0L), probe.Outcome);
        }
    }

    [Fact]
    public void WhenTheObservedRevisionIsStillCurrent_ThenTheConditionalWriteCommits()
    {
        // Arrange
        var probe = new WriteOutcomeProbe();
        var context = InterceptorSubjectContext.Create().WithService(() => probe);
        var subject = new OriginProbeSubject(context) { Name = "observed" };
        var property = new PropertyReference(subject, nameof(OriginProbeSubject.Name));
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var observedRevision, out _));
        probe.Reset();

        // Act
        var written = property.TrySetValueIfCommitRevisionIs("restored", observedRevision, includeSourceCommits: false);

        // Assert
        Assert.True(written);
        Assert.Equal("restored", subject.Name);
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var revisionAfter, out _));
        Assert.True(revisionAfter > observedRevision);
        Assert.Equal((true, revisionAfter), probe.Outcome);
    }

    [Fact]
    public void WhenAConditionalWriteIsSkipped_ThenAnOrdinaryWriteAfterwardsIsUnconditional()
    {
        // Arrange: the precondition must not leak from the consumed slot into the next write.
        var context = InterceptorSubjectContext.Create();
        var subject = new OriginProbeSubject(context) { Name = "first" };
        var property = new PropertyReference(subject, nameof(OriginProbeSubject.Name));
        Assert.False(property.TrySetValueIfCommitRevisionIs("stale", expectedCommitRevision: -1, includeSourceCommits: false));

        // Act
        subject.Name = "second";

        // Assert
        Assert.Equal("second", subject.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAChangingHookWritesAnotherPropertyDuringASkippedConditionalWrite_ThenThatWriteIsUnconditional(bool withWriteInterceptor)
    {
        // Arrange: the hook runs before the conditional write's context consumes the armed slot, so a
        // precondition that leaked onto the hook's write would skip it, since no revision is -1.
        var context = InterceptorSubjectContext.Create();
        if (withWriteInterceptor)
        {
            context.WithService(() => new WriteOutcomeProbe());
        }

        var subject = new HookedPreconditionSubject(context) { Primary = "primary", Secondary = "secondary" };
        subject.BeforePrimaryChanging = () => subject.Secondary = "from-hook";
        var primary = new PropertyReference(subject, nameof(HookedPreconditionSubject.Primary));

        // Act
        var written = primary.TrySetValueIfCommitRevisionIs("stale", expectedCommitRevision: -1, includeSourceCommits: false);

        // Assert
        Assert.False(written);
        Assert.Equal("primary", subject.Primary);
        Assert.Equal("from-hook", subject.Secondary);
    }

    [Fact]
    public void WhenAWriteInterceptorWritesAnotherPropertyDuringASkippedConditionalWrite_ThenThatWriteIsUnconditional()
    {
        // Arrange: the interceptor writes before calling next, between the consumption and the terminal.
        HookedPreconditionSubject? subject = null;
        var interceptor = new BeforeNextWriteInterceptor(nameof(HookedPreconditionSubject.Primary), () => subject!.Secondary = "from-interceptor");
        var context = InterceptorSubjectContext.Create().WithService(() => interceptor);
        subject = new HookedPreconditionSubject(context) { Primary = "primary", Secondary = "secondary" };
        interceptor.IsArmed = true;
        var primary = new PropertyReference(subject, nameof(HookedPreconditionSubject.Primary));

        // Act
        var written = primary.TrySetValueIfCommitRevisionIs("stale", expectedCommitRevision: -1, includeSourceCommits: false);

        // Assert
        Assert.False(written);
        Assert.Equal("primary", subject.Primary);
        Assert.Equal("from-interceptor", subject.Secondary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenANestedConditionalWriteIsSkipped_ThenTheOuterConditionalWriteReportsItsOwnOutcome(bool fromInterceptor)
    {
        // Arrange: the nested conditional write fails and marks its own frame, from the hook before the
        // outer write consumes its slot or from an interceptor after it did. The outer write must not
        // read that mark once the nested scope restores the outer frame.
        HookedPreconditionSubject? subject = null;
        bool? nestedWritten = null;
        Action nestedWrite = () => nestedWritten = new PropertyReference(subject!, nameof(HookedPreconditionSubject.Secondary))
            .TrySetValueIfCommitRevisionIs("nested", expectedCommitRevision: -1, includeSourceCommits: false);

        var interceptor = new BeforeNextWriteInterceptor(nameof(HookedPreconditionSubject.Primary), nestedWrite);
        var context = InterceptorSubjectContext.Create().WithService(() => interceptor);
        subject = new HookedPreconditionSubject(context) { Primary = "primary", Secondary = "secondary" };
        if (fromInterceptor)
        {
            interceptor.IsArmed = true;
        }
        else
        {
            subject.BeforePrimaryChanging = nestedWrite;
        }

        var primary = new PropertyReference(subject, nameof(HookedPreconditionSubject.Primary));
        Assert.True(primary.TryGetWriteState(includeSourceCommitsInRevision: false, out var primaryRevision, out _));

        // Act
        var written = primary.TrySetValueIfCommitRevisionIs("outer", primaryRevision, includeSourceCommits: false);

        // Assert
        Assert.False(nestedWritten);
        Assert.True(written);
        Assert.Equal("outer", subject.Primary);
        Assert.Equal("secondary", subject.Secondary);
    }

    [Fact]
    public void WhenANestedConditionalWriteCommitsAfterTheOuterWasSkipped_ThenTheOuterStillReportsTheSkip()
    {
        // Arrange: the outer terminal marks the frame before the interceptor's nested conditional write
        // runs; that nested scope must hand the mark back when it restores the outer frame.
        HookedPreconditionSubject? subject = null;
        bool? nestedWritten = null;
        var interceptor = new AfterNextWriteInterceptor(nameof(HookedPreconditionSubject.Primary), () =>
        {
            var secondary = new PropertyReference(subject!, nameof(HookedPreconditionSubject.Secondary));
            secondary.TryGetWriteState(includeSourceCommitsInRevision: false, out var secondaryRevision, out _);
            nestedWritten = secondary.TrySetValueIfCommitRevisionIs("nested", secondaryRevision, includeSourceCommits: false);
        });
        var context = InterceptorSubjectContext.Create().WithService(() => interceptor);
        subject = new HookedPreconditionSubject(context) { Primary = "primary", Secondary = "secondary" };
        interceptor.IsArmed = true;
        var primary = new PropertyReference(subject, nameof(HookedPreconditionSubject.Primary));

        // Act
        var written = primary.TrySetValueIfCommitRevisionIs("stale", expectedCommitRevision: -1, includeSourceCommits: false);

        // Assert
        Assert.True(nestedWritten);
        Assert.False(written);
        Assert.Equal("primary", subject.Primary);
        Assert.Equal("nested", subject.Secondary);
    }

    private sealed class BeforeNextWriteInterceptor(string propertyName, Action write) : IWriteInterceptor
    {
        public bool IsArmed { get; set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (IsArmed && context.Property.Name == propertyName)
            {
                IsArmed = false;
                write();
            }

            next(ref context);
        }
    }

    private sealed class AfterNextWriteInterceptor(string propertyName, Action write) : IWriteInterceptor
    {
        public bool IsArmed { get; set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            if (IsArmed && context.Property.Name == propertyName)
            {
                IsArmed = false;
                write();
            }
        }
    }

    private sealed class WriteOutcomeProbe : IWriteInterceptor
    {
        public (bool IsWritten, long Revision)? Outcome { get; private set; }

        public void Reset() => Outcome = null;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            Outcome = (context.IsWritten, context.Revision);
        }
    }
}

[InterceptorSubject]
public partial class HookedPreconditionSubject
{
    public Action? BeforePrimaryChanging { get; set; }

    public partial string? Primary { get; set; }

    public partial string? Secondary { get; set; }

    partial void OnPrimaryChanging(ref string? newValue, ref bool cancel)
    {
        BeforePrimaryChanging?.Invoke();
    }
}
