using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class WriteTimestampTests
{
    [Fact]
    public void WriteTimestamp_BeforeAnyWrite_ShouldBeNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Act
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();

        // Assert
        Assert.Null(timestamp);
    }

    [Fact]
    public void WriteTimestamp_AfterWrite_ShouldBeSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var before = DateTimeOffset.UtcNow;

        // Act
        person.FirstName = "John";

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
    }

    [Fact]
    public void WriteTimestamp_WithExplicitTimestamp_ShouldMatchExplicitValue()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-100);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            person.FirstName = "John";
        }

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.Equal(explicitTimestamp, timestamp);
    }

    [Fact]
    public void WriteTimestamp_SecondWriteOverwritesFirst()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstTimestamp = DateTimeOffset.UtcNow.AddDays(-100);
        var secondTimestamp = DateTimeOffset.UtcNow.AddDays(-50);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            person.FirstName = "Jane";
        }

        // Assert
        var timestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
        Assert.Equal(secondTimestamp, timestamp);
    }

    [Fact]
    public void WriteTimestamp_DifferentProperties_HaveIndependentTimestamps()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstTimestamp = DateTimeOffset.UtcNow.AddDays(-100);
        var secondTimestamp = DateTimeOffset.UtcNow.AddDays(-50);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
        {
            person.FirstName = "John";
        }

        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            person.LastName = "Doe";
        }

        // Assert
        Assert.Equal(firstTimestamp, person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
        Assert.Equal(secondTimestamp, person.GetPropertyReference("LastName").TryGetWriteTimestamp());
    }

    [Fact]
    public void WriteTimestamp_WithExplicitTimestamp_ChangeEventsShouldMatch()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        var timestamp = DateTimeOffset.UtcNow.AddDays(-200);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            person.FirstName = "Mother";
        }

        // Assert
        Assert.Equal(3, changes.Count); // backed, derived, derived
        Assert.True(changes.All(c => c.ChangedTimestamp == timestamp));
    }

    [Fact]
    public void WriteTimestamp_ConcurrentWrites_ValueAndTimestampAreConsistent()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var syncRoot = ((IInterceptorSubject)person).SyncRoot;

        // Each thread writes a unique value with a matching unique timestamp.
        // After each write, we read value + timestamp under the same lock to verify
        // they belong to the same write operation.
        const int threadCount = 8;
        const int iterationsPerThread = 5_000;
        var barrier = new Barrier(threadCount);
        var failures = 0;

        // Act
        var threads = Enumerable.Range(0, threadCount).Select(threadIndex => new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterationsPerThread; i++)
            {
                var index = threadIndex * iterationsPerThread + i;
                var timestamp = new DateTimeOffset(index + 1, TimeSpan.Zero); // ticks = index+1 (avoid 0)

                using (SubjectChangeContext.WithChangedTimestamp(timestamp))
                {
                    person.FirstName = $"Name{index}";
                }

                // Read value + timestamp under the same lock that protects writes
                lock (syncRoot)
                {
                    var storedTimestamp = person.GetPropertyReference("FirstName").TryGetWriteTimestamp();
                    var storedValue = person.FirstName;

                    if (storedTimestamp.HasValue)
                    {
                        var expectedIndex = (int)(storedTimestamp.Value.UtcTicks - 1);
                        var expectedValue = $"Name{expectedIndex}";
                        if (storedValue != expectedValue)
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                }
            }
        })).ToArray();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        // Assert
        Assert.Equal(0, failures);
    }

    [Fact]
    public void WriteTimestamp_WithNullTimestamp_ShouldReturnNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            person.FirstName = "John";
        }

        // Assert
        Assert.Null(person.GetPropertyReference("FirstName").TryGetWriteTimestamp());
    }

    [Fact]
    public void WriteTimestamp_AfterScopeEnds_ShouldUseCurrentTime()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-200);

        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            person.FirstName = "First";
        }

        // Act
        var before = DateTimeOffset.UtcNow;
        person.LastName = "Second";

        // Assert
        var timestamp = person.GetPropertyReference("LastName").TryGetWriteTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
        Assert.NotEqual(explicitTimestamp, timestamp);
    }
}
