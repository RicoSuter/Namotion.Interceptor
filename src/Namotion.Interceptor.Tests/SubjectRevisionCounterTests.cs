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
        var firstA = SubjectRevisionCounter.Next(first);
        var firstB = SubjectRevisionCounter.Next(first);
        var secondA = SubjectRevisionCounter.Next(second);

        // Assert
        Assert.Equal(1, firstA);
        Assert.Equal(2, firstB);
        Assert.Equal(1, secondA); // independent per subject
    }

    [Fact]
    public void WhenContextIsNotAnInterceptorExecutor_ThenRevisionStillIncrements()
    {
        // Arrange
        var subject = new PlainSubject();

        // Act
        var first = SubjectRevisionCounter.Next(subject);
        var second = SubjectRevisionCounter.Next(subject);

        // Assert
        Assert.Equal(1, first);
        Assert.Equal(2, second);
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
