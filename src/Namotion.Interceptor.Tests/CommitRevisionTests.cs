using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class CommitRevisionTests
{
    [Fact]
    public void WhenPropertiesWrittenThroughChain_ThenContextCarriesDenseIncreasingRevisions()
    {
        // Arrange
        var revisions = new List<long>();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new RevisionCapturingInterceptor(revisions));
        var subject = new OriginProbeSubject(context);

        // Act: distinct values so every write actually commits.
        subject.Name = "first";
        subject.Name = "second";
        subject.Mode = ProbeMode.Running;

        // Assert
        Assert.Equal(new long[] { 1, 2, 3 }, revisions);
    }

    [Fact]
    public void WhenWrittenOnContextWithoutWriteInterceptors_ThenTerminalStillAssignsRevisions()
    {
        // Arrange: no registered write interceptor, so writes take the zero-interceptor terminal,
        // which no capturing interceptor can observe. The executor's counter is read instead.
        var context = InterceptorSubjectContext.Create();
        var written = new OriginProbeSubject(context);
        var untouched = new OriginProbeSubject(context);

        // Act
        written.Name = "first";
        written.Name = "second";
        written.Mode = ProbeMode.Running;

        // Assert: the terminal assigned one revision per committed write, while an unwritten subject
        // sharing the same context stayed at zero, so the counter is per subject.
        Assert.Equal(3, ((InterceptorExecutor)((IInterceptorSubject)written).Context).Revision);
        Assert.Equal(0, ((InterceptorExecutor)((IInterceptorSubject)untouched).Context).Revision);
    }

    private sealed class RevisionCapturingInterceptor(List<long> revisions) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            revisions.Add(context.Revision);
        }
    }
}
