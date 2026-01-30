namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for ConnectorReferenceCounter focusing on reference counting correctness and thread-safety.
/// Verifies that the counter properly tracks first/last references and associated data.
/// </summary>
public class ConnectorReferenceCounterTests
{
    [Fact]
    public void IncrementAndCheckFirst_FirstReference_ReturnsTrue()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();

        // Act
        var isFirst = counter.IncrementAndCheckFirst(subject, () => "data", out var data);

        // Assert
        Assert.True(isFirst);
        Assert.Equal("data", data);
    }

    [Fact]
    public void IncrementAndCheckFirst_SecondReference_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);

        // Act
        var isFirst = counter.IncrementAndCheckFirst(subject, () => "new-data", out var data);

        // Assert
        Assert.False(isFirst);
        Assert.Equal("data", data); // Original data, not new
    }

    [Fact]
    public void DecrementAndCheckLast_LastReference_ReturnsTrue()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);

        // Act
        var isLast = counter.DecrementAndCheckLast(subject, out var data);

        // Assert
        Assert.True(isLast);
        Assert.Equal("data", data);
    }

    [Fact]
    public void DecrementAndCheckLast_NotLastReference_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);
        counter.IncrementAndCheckFirst(subject, () => "data", out _); // Second ref

        // Act
        var isLast = counter.DecrementAndCheckLast(subject, out var data);

        // Assert
        Assert.False(isLast);
        Assert.Null(data);
    }

    [Fact]
    public void DecrementAndCheckLast_NoReference_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();

        // Act
        var isLast = counter.DecrementAndCheckLast(subject, out var data);

        // Assert
        Assert.False(isLast);
        Assert.Null(data);
    }

    [Fact]
    public void TryGetData_ExistingSubject_ReturnsData()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);

        // Act
        var found = counter.TryGetData(subject, out var data);

        // Assert
        Assert.True(found);
        Assert.Equal("data", data);
    }

    [Fact]
    public void TryGetData_NonExistingSubject_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();

        // Act
        var found = counter.TryGetData(subject, out var data);

        // Assert
        Assert.False(found);
        Assert.Null(data);
    }

    [Fact]
    public void Clear_ReturnsAllDataForCleanup()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject1 = new TestSubject();
        var subject2 = new TestSubject();
        counter.IncrementAndCheckFirst(subject1, () => "data1", out _);
        counter.IncrementAndCheckFirst(subject2, () => "data2", out _);

        // Act
        var clearedData = counter.Clear().ToList();

        // Assert
        Assert.Equal(2, clearedData.Count);
        Assert.Contains("data1", clearedData);
        Assert.Contains("data2", clearedData);
    }

    [Fact]
    public void Clear_EmptiesCounter()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);
        counter.Clear();

        // Act
        var found = counter.TryGetData(subject, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public async Task IncrementAndCheckFirst_ConcurrentAccess_CountsCorrectly()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<int>();
        var subject = new TestSubject();
        var tasks = new List<Task>();
        var firstCount = 0;

        // Act - 100 concurrent increments
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var isFirst = counter.IncrementAndCheckFirst(subject, () => 42, out _);
                if (isFirst) Interlocked.Increment(ref firstCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be first
        Assert.Equal(1, firstCount);
    }

    [Fact]
    public async Task DecrementAndCheckLast_ConcurrentAccess_CountsCorrectly()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<int>();
        var subject = new TestSubject();

        // Add 100 references
        for (int i = 0; i < 100; i++)
        {
            counter.IncrementAndCheckFirst(subject, () => 42, out _);
        }

        var tasks = new List<Task>();
        var lastCount = 0;

        // Act - 100 concurrent decrements
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var isLast = counter.DecrementAndCheckLast(subject, out _);
                if (isLast) Interlocked.Increment(ref lastCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be last
        Assert.Equal(1, lastCount);

        // Counter should be empty
        Assert.False(counter.TryGetData(subject, out _));
    }

    [Fact]
    public void GetAllEntries_ReturnsAllSubjectsAndData()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject1 = new TestSubject();
        var subject2 = new TestSubject();
        counter.IncrementAndCheckFirst(subject1, () => "data1", out _);
        counter.IncrementAndCheckFirst(subject2, () => "data2", out _);

        // Act
        var entries = counter.GetAllEntries().ToList();

        // Assert
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => ReferenceEquals(e.Subject, subject1) && e.Data == "data1");
        Assert.Contains(entries, e => ReferenceEquals(e.Subject, subject2) && e.Data == "data2");
    }

    [Fact]
    public void GetAllEntries_EmptyCounter_ReturnsEmpty()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();

        // Act
        var entries = counter.GetAllEntries().ToList();

        // Assert
        Assert.Empty(entries);
    }

    private class TestSubject : IInterceptorSubject
    {
        public object SyncRoot => new object();
        public IInterceptorSubjectContext Context => throw new NotImplementedException();
        public System.Collections.Concurrent.ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotImplementedException();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotImplementedException();
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotImplementedException();
    }
}
