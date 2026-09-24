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
