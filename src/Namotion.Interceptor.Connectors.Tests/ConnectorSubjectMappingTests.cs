using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for ConnectorSubjectMapping focusing on bidirectional mapping,
/// reference counting correctness, and thread-safety.
/// </summary>
public class ConnectorSubjectMappingTests
{
    [Fact]
    public void Register_FirstReference_ReturnsTrue()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);

        // Act
        var isFirst = registry.Register(subject, "node-1");

        // Assert
        Assert.True(isFirst);
    }

    [Fact]
    public void Register_SecondReference_ReturnsFalse()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");

        // Act
        var isFirst = registry.Register(subject, "node-1");

        // Assert
        Assert.False(isFirst);
    }

    [Fact]
    public void Unregister_LastReference_ReturnsTrue()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");

        // Act
        var isLast = registry.Unregister(subject, out var externalId);

        // Assert
        Assert.True(isLast);
        Assert.Equal("node-1", externalId);
    }

    [Fact]
    public void Unregister_NotLastReference_ReturnsFalse()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");
        registry.Register(subject, "node-1");

        // Act
        var isLast = registry.Unregister(subject, out var externalId);

        // Assert
        Assert.False(isLast);
        Assert.Null(externalId);
    }

    [Fact]
    public void Unregister_NoReference_ReturnsFalse()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);

        // Act
        var isLast = registry.Unregister(subject, out var externalId);

        // Assert
        Assert.False(isLast);
        Assert.Null(externalId);
    }

    [Fact]
    public void TryGetExternalId_RegisteredSubject_ReturnsTrue()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");

        // Act
        var found = registry.TryGetExternalId(subject, out var externalId);

        // Assert
        Assert.True(found);
        Assert.Equal("node-1", externalId);
    }

    [Fact]
    public void TryGetExternalId_UnregisteredSubject_ReturnsFalse()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);

        // Act
        var found = registry.TryGetExternalId(subject, out var externalId);

        // Assert
        Assert.False(found);
        Assert.Null(externalId);
    }

    [Fact]
    public void TryGetSubject_RegisteredId_ReturnsTrue()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");

        // Act
        var found = registry.TryGetSubject("node-1", out var result);

        // Assert
        Assert.True(found);
        Assert.Same(subject, result);
    }

    [Fact]
    public void TryGetSubject_UnregisteredId_ReturnsFalse()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();

        // Act
        var found = registry.TryGetSubject("node-1", out var result);

        // Assert
        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void GetAllSubjects_ReturnsAllRegisteredSubjects()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject1 = new Person(context);
        var subject2 = new Person(context);
        registry.Register(subject1, "node-1");
        registry.Register(subject2, "node-2");

        // Act
        var subjects = registry.GetAllSubjects().ToList();

        // Assert
        Assert.Equal(2, subjects.Count);
        Assert.Contains(subject1, subjects);
        Assert.Contains(subject2, subjects);
    }

    [Fact]
    public void GetAllSubjects_EmptyRegistry_ReturnsEmpty()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();

        // Act
        var subjects = registry.GetAllSubjects().ToList();

        // Assert
        Assert.Empty(subjects);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");

        // Act
        registry.Clear();

        // Assert
        Assert.False(registry.TryGetExternalId(subject, out _));
        Assert.False(registry.TryGetSubject("node-1", out _));
        Assert.Empty(registry.GetAllSubjects());
    }

    [Fact]
    public async Task Register_ConcurrentAccess_CountsCorrectly()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        var tasks = new List<Task>();
        var firstCount = 0;

        // Act - 100 concurrent registrations
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var isFirst = registry.Register(subject, "node-1");
                if (isFirst) Interlocked.Increment(ref firstCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be first
        Assert.Equal(1, firstCount);
    }

    [Fact]
    public async Task Unregister_ConcurrentAccess_CountsCorrectly()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);

        // Add 100 references
        for (int i = 0; i < 100; i++)
        {
            registry.Register(subject, "node-1");
        }

        var tasks = new List<Task>();
        var lastCount = 0;

        // Act - 100 concurrent unregistrations
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var isLast = registry.Unregister(subject, out _);
                if (isLast) Interlocked.Increment(ref lastCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be last
        Assert.Equal(1, lastCount);

        // Registry should be empty
        Assert.False(registry.TryGetExternalId(subject, out _));
        Assert.False(registry.TryGetSubject("node-1", out _));
    }

    [Fact]
    public void Register_MultipleSubjects_MaintainsBidirectionalMapping()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<int>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject1 = new Person(context);
        var subject2 = new Person(context);
        var subject3 = new Person(context);

        // Act
        registry.Register(subject1, 100);
        registry.Register(subject2, 200);
        registry.Register(subject3, 300);

        // Assert - forward lookup
        Assert.True(registry.TryGetExternalId(subject1, out var id1));
        Assert.Equal(100, id1);
        Assert.True(registry.TryGetExternalId(subject2, out var id2));
        Assert.Equal(200, id2);
        Assert.True(registry.TryGetExternalId(subject3, out var id3));
        Assert.Equal(300, id3);

        // Assert - reverse lookup
        Assert.True(registry.TryGetSubject(100, out var s1));
        Assert.Same(subject1, s1);
        Assert.True(registry.TryGetSubject(200, out var s2));
        Assert.Same(subject2, s2);
        Assert.True(registry.TryGetSubject(300, out var s3));
        Assert.Same(subject3, s3);
    }

    [Fact]
    public void Unregister_RemovesMappingOnlyWhenLast()
    {
        // Arrange
        var registry = new ConnectorSubjectMapping<string>();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new Person(context);
        registry.Register(subject, "node-1");
        registry.Register(subject, "node-1"); // Second reference

        // Act - first unregister
        var isLast1 = registry.Unregister(subject, out var id1);

        // Assert - mapping still exists
        Assert.False(isLast1);
        Assert.Null(id1);
        Assert.True(registry.TryGetExternalId(subject, out _));
        Assert.True(registry.TryGetSubject("node-1", out _));

        // Act - second unregister
        var isLast2 = registry.Unregister(subject, out var id2);

        // Assert - mapping removed
        Assert.True(isLast2);
        Assert.Equal("node-1", id2);
        Assert.False(registry.TryGetExternalId(subject, out _));
        Assert.False(registry.TryGetSubject("node-1", out _));
    }
}
