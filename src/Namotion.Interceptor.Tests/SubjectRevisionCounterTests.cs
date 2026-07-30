using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class SubjectRevisionCounterTests
{
    [Fact]
    public void WhenIncrementedRepeatedly_ThenRevisionIsMonotonicPerSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var first = new Car(context);
        var second = new Car(context);

        // Act
        var firstA = NextUnderSyncRoot(first);
        var firstB = NextUnderSyncRoot(first);
        var secondA = NextUnderSyncRoot(second);

        // Assert
        Assert.Equal(1, firstA);
        Assert.Equal(2, firstB);
        Assert.Equal(1, secondA); // independent per subject
    }

    [Fact]
    public void WhenContextIsNotAnInterceptorExecutor_ThenRevisionStillIncrements()
    {
        // Arrange
        var first = new PlainSubject();
        var second = new PlainSubject();

        // Act
        var firstA = NextUnderSyncRoot(first);
        var firstB = NextUnderSyncRoot(first);
        var secondA = NextUnderSyncRoot(second);

        // Assert
        Assert.Equal(1, firstA);
        Assert.Equal(2, firstB);
        Assert.Equal(1, secondA); // independent per subject
    }

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
        // which no capturing interceptor can observe.
        var context = InterceptorSubjectContext.Create();
        var written = new OriginProbeSubject(context);
        var untouched = new OriginProbeSubject(context);

        // Act
        written.Name = "first";
        written.Name = "second";
        written.Mode = ProbeMode.Running;

        // Assert: the counter continues after the three revisions the terminal assigned, while an
        // unwritten subject on the same context still starts at 1.
        Assert.Equal(4, NextUnderSyncRoot(written));
        Assert.Equal(1, NextUnderSyncRoot(untouched));
    }

    private sealed class RevisionCapturingInterceptor(List<long> revisions) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            revisions.Add(context.Revision);
        }
    }

    /// <summary>
    /// The counter documents that callers hold the subject's SyncRoot, and the assertion inside
    /// <see cref="SubjectRevisionCounter.Next"/> enforces it, so the tests take the same lock.
    /// </summary>
    private static long NextUnderSyncRoot(IInterceptorSubject subject)
    {
        lock (subject.SyncRoot)
        {
            return SubjectRevisionCounter.Next(subject);
        }
    }

    /// <summary>
    /// Hand-written subject that does not create an <see cref="Interceptors.InterceptorExecutor"/>,
    /// exercising the subject data fallback path.
    /// </summary>
    private sealed class PlainSubject : IInterceptorSubject
    {
        public object SyncRoot { get; } = new();

        public IInterceptorSubjectContext Context { get; } = new InterceptorSubjectContext();

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } =
            new Dictionary<string, SubjectPropertyMetadata>();

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }
}
