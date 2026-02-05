using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for SubjectConnectorRegistry covering registration, unregistration,
/// lookups, modifications, and thread-safety.
/// </summary>
public class SubjectConnectorRegistryTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithRegistry();

    [Fact]
    public void Register_FirstReference_ReturnsIsFirstTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.Register(subject, "node-1", () => [], out var data, out var isFirst);

        // Assert
        Assert.True(result);
        Assert.True(isFirst);
        Assert.NotNull(data);
    }

    [Fact]
    public void Register_SecondReference_ReturnsIsFirstFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => [1], out _, out _);

        // Act
        var result = registry.Register(subject, "node-1", () => [2], out var data, out var isFirst);

        // Assert
        Assert.True(result);
        Assert.False(isFirst);
        Assert.Single(data); // Should return existing data, not new
        Assert.Equal(1, data[0]);
    }

    [Fact]
    public void Register_NullSubject_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();

        // Act
        var result = registry.Register(null!, "node-1", () => [], out var data, out var isFirst);

        // Assert
        Assert.False(result);
        Assert.False(isFirst);
    }

    [Fact]
    public void Register_CallsDataFactoryOnlyOnFirstReference()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        var callCount = 0;

        // Act
        registry.Register(subject, "node-1", () => { callCount++; return 100; }, out var data1, out _);
        registry.Register(subject, "node-1", () => { callCount++; return 200; }, out var data2, out _);

        // Assert
        Assert.Equal(1, callCount);
        Assert.Equal(100, data1);
        Assert.Equal(100, data2);
    }

    [Fact]
    public void Unregister_LastReference_ReturnsWasLastTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.Unregister(subject, out var externalId, out var data, out var wasLast);

        // Assert
        Assert.True(result);
        Assert.True(wasLast);
        Assert.Equal("node-1", externalId);
        Assert.Equal(42, data);
    }

    [Fact]
    public void Unregister_NotLastReference_ReturnsWasLastFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);
        registry.Register(subject, "node-1", () => 99, out _, out _);

        // Act
        var result = registry.Unregister(subject, out var externalId, out var data, out var wasLast);

        // Assert
        Assert.True(result);
        Assert.False(wasLast);
        Assert.Null(externalId);
        Assert.Equal(default, data);
    }

    [Fact]
    public void Unregister_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.Unregister(subject, out var externalId, out var data, out var wasLast);

        // Assert
        Assert.False(result);
        Assert.False(wasLast);
    }

    [Fact]
    public void Unregister_RemovesMappingOnlyWhenLast()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act - first unregister
        registry.Unregister(subject, out _, out _, out _);

        // Assert - still registered
        Assert.True(registry.TryGetExternalId(subject, out _));

        // Act - second unregister
        registry.Unregister(subject, out _, out _, out _);

        // Assert - now removed
        Assert.False(registry.TryGetExternalId(subject, out _));
    }

    [Fact]
    public void UnregisterByExternalId_Registered_ReturnsSubject()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.UnregisterByExternalId("node-1", out var foundSubject, out var data, out var wasLast);

        // Assert
        Assert.True(result);
        Assert.Same(subject, foundSubject);
        Assert.Equal(42, data);
        Assert.True(wasLast);
    }

    [Fact]
    public void UnregisterByExternalId_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();

        // Act
        var result = registry.UnregisterByExternalId("node-1", out var subject, out _, out _);

        // Assert
        Assert.False(result);
        Assert.Null(subject);
    }

    [Fact]
    public void TryGetExternalId_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.TryGetExternalId(subject, out var externalId);

        // Assert
        Assert.True(result);
        Assert.Equal("node-1", externalId);
    }

    [Fact]
    public void TryGetExternalId_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.TryGetExternalId(subject, out var externalId);

        // Assert
        Assert.False(result);
        Assert.Null(externalId);
    }

    [Fact]
    public void TryGetSubject_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.TryGetSubject("node-1", out var foundSubject);

        // Assert
        Assert.True(result);
        Assert.Same(subject, foundSubject);
    }

    [Fact]
    public void TryGetData_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.TryGetData(subject, out var data);

        // Assert
        Assert.True(result);
        Assert.Equal(42, data);
    }

    [Fact]
    public void IsRegistered_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act & Assert
        Assert.True(registry.IsRegistered(subject));
    }

    [Fact]
    public void IsRegistered_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Act & Assert
        Assert.False(registry.IsRegistered(subject));
    }

    [Fact]
    public void UpdateExternalId_Registered_UpdatesBothDirections()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.UpdateExternalId(subject, "node-2");

        // Assert
        Assert.True(result);
        Assert.True(registry.TryGetExternalId(subject, out var newId));
        Assert.Equal("node-2", newId);
        Assert.True(registry.TryGetSubject("node-2", out var foundSubject));
        Assert.Same(subject, foundSubject);
        Assert.False(registry.TryGetSubject("node-1", out _)); // Old ID removed
    }

    [Fact]
    public void UpdateExternalId_Collision_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject1 = new Person(CreateContext());
        var subject2 = new Person(CreateContext());
        registry.Register(subject1, "node-1", () => 1, out _, out _);
        registry.Register(subject2, "node-2", () => 2, out _, out _);

        // Act - try to give subject1 the same ID as subject2
        var result = registry.UpdateExternalId(subject1, "node-2");

        // Assert
        Assert.False(result);
        // Original mappings unchanged
        Assert.True(registry.TryGetExternalId(subject1, out var id1));
        Assert.Equal("node-1", id1);
    }

    [Fact]
    public void ModifyData_Registered_ExecutesModifier()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => [1, 2], out _, out _);

        // Act
        var result = registry.ModifyData(subject, list => list.Add(3));

        // Assert
        Assert.True(result);
        Assert.True(registry.TryGetData(subject, out var data));
        Assert.Equal([1, 2, 3], data);
    }

    [Fact]
    public void ModifyData_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.ModifyData(subject, list => list.Add(1));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetAllSubjects_ReturnsAllRegistered()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject1 = new Person(CreateContext());
        var subject2 = new Person(CreateContext());
        registry.Register(subject1, "node-1", () => 1, out _, out _);
        registry.Register(subject2, "node-2", () => 2, out _, out _);

        // Act
        var subjects = registry.GetAllSubjects();

        // Assert
        Assert.Equal(2, subjects.Count);
        Assert.Contains(subject1, subjects);
        Assert.Contains(subject2, subjects);
    }

    [Fact]
    public void GetAllEntries_ReturnsAllData()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var entries = registry.GetAllEntries();

        // Assert
        Assert.Single(entries);
        Assert.Same(subject, entries[0].Subject);
        Assert.Equal("node-1", entries[0].ExternalId);
        Assert.Equal(42, entries[0].Data);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        registry.Clear();

        // Assert
        Assert.False(registry.IsRegistered(subject));
        Assert.False(registry.TryGetSubject("node-1", out _));
        Assert.Empty(registry.GetAllSubjects());
    }

    [Fact]
    public async Task Register_ConcurrentAccess_ExactlyOneFirst()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        var firstCount = 0;
        var tasks = new List<Task>();

        // Act - 100 concurrent registrations
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                registry.Register(subject, "node-1", () => 42, out _, out var isFirst);
                if (isFirst) Interlocked.Increment(ref firstCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be first
        Assert.Equal(1, firstCount);
    }

    [Fact]
    public async Task Unregister_ConcurrentAccess_ExactlyOneLast()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Add 100 references
        for (int i = 0; i < 100; i++)
        {
            registry.Register(subject, "node-1", () => 42, out _, out _);
        }

        var lastCount = 0;
        var tasks = new List<Task>();

        // Act - 100 concurrent unregistrations
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                registry.Unregister(subject, out _, out _, out var wasLast);
                if (wasLast) Interlocked.Increment(ref lastCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be last
        Assert.Equal(1, lastCount);
        Assert.False(registry.IsRegistered(subject));
    }

    [Fact]
    public async Task ModifyData_ConcurrentAccess_AllModificationsApplied()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => [], out _, out _);
        var tasks = new List<Task>();

        // Act - 100 concurrent modifications
        for (int i = 0; i < 100; i++)
        {
            var value = i;
            tasks.Add(Task.Run(() =>
            {
                registry.ModifyData(subject, list => list.Add(value));
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - all modifications applied
        Assert.True(registry.TryGetData(subject, out var data));
        Assert.Equal(100, data!.Count);
    }
}
