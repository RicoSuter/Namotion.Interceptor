using System.Collections.Concurrent;

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
