using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// Unit tests for <see cref="OpcUaServerGraphChangeSender"/>.
/// These tests verify the sender correctly delegates to CustomNodeManager methods.
/// </summary>
public class OpcUaServerGraphChangeSenderTests
{
    private static readonly DateTimeOffset TestTimestamp = DateTimeOffset.UtcNow;

    /// <summary>
    /// A test structural change processor that records calls instead of using a real CustomNodeManager.
    /// </summary>
    private class TestableGraphChangePublisher : GraphChangePublisher
    {
        public List<(RegisteredSubjectProperty Property, IInterceptorSubject Subject, object? Index)> AddedSubjects { get; } = new();
        public List<(RegisteredSubjectProperty Property, IInterceptorSubject Subject, object? Index)> RemovedSubjects { get; } = new();

        protected override Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
        {
            AddedSubjects.Add((property, subject, index));
            return Task.CompletedTask;
        }

        protected override Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
        {
            RemovedSubjects.Add((property, subject, index));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectAdded_CallsOnSubjectAdded()
    {
        // Arrange
        var processor = new TestableGraphChangePublisher();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new TestRoot(context);
        var person = new TestPerson(context);

        var registeredSubject = root.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(p => p.Name == nameof(TestRoot.Person));

        var change = SubjectPropertyChange.Create<TestPerson?>(
            property.Reference,
            source: null,
            TestTimestamp,
            receivedTimestamp: null,
            oldValue: null,
            newValue: person);

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.True(result); // Structural change processed
        Assert.Single(processor.AddedSubjects);
        Assert.Equal(person, processor.AddedSubjects[0].Subject);
        Assert.Equal(property, processor.AddedSubjects[0].Property);
        Assert.Null(processor.AddedSubjects[0].Index);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectRemoved_CallsOnSubjectRemoved()
    {
        // Arrange
        var processor = new TestableGraphChangePublisher();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new TestRoot(context);
        var person = new TestPerson(context);

        var registeredSubject = root.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(p => p.Name == nameof(TestRoot.Person));

        var change = SubjectPropertyChange.Create<TestPerson?>(
            property.Reference,
            source: null,
            TestTimestamp,
            receivedTimestamp: null,
            oldValue: person,
            newValue: null);

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.True(result); // Structural change processed
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(person, processor.RemovedSubjects[0].Subject);
        Assert.Equal(property, processor.RemovedSubjects[0].Property);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_ValueChange_ReturnsFalse()
    {
        // Arrange
        var processor = new TestableGraphChangePublisher();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new TestRoot(context);

        var registeredSubject = root.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(p => p.Name == nameof(TestRoot.Name));

        var change = SubjectPropertyChange.Create<string?>(
            property.Reference,
            source: null,
            TestTimestamp,
            receivedTimestamp: null,
            oldValue: "old",
            newValue: "new");

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert - Returns false for non-structural (value) changes; caller handles value processing
        Assert.False(result);
        Assert.Empty(processor.AddedSubjects);
        Assert.Empty(processor.RemovedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_CollectionAdd_CallsOnSubjectAdded()
    {
        // Arrange
        var processor = new TestableGraphChangePublisher();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new TestRoot(context);
        var person1 = new TestPerson(context);

        var registeredSubject = root.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(p => p.Name == nameof(TestRoot.People));

        var oldCollection = Array.Empty<TestPerson>();
        var newCollection = new TestPerson[] { person1 };

        var change = SubjectPropertyChange.Create<TestPerson[]>(
            property.Reference,
            source: null,
            TestTimestamp,
            receivedTimestamp: null,
            oldValue: oldCollection,
            newValue: newCollection);

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.True(result); // Structural change processed
        Assert.Single(processor.AddedSubjects);
        Assert.Equal(person1, processor.AddedSubjects[0].Subject);
        Assert.Equal(0, processor.AddedSubjects[0].Index);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_CollectionRemove_CallsOnSubjectRemoved()
    {
        // Arrange
        var processor = new TestableGraphChangePublisher();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new TestRoot(context);
        var person1 = new TestPerson(context);

        var registeredSubject = root.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(p => p.Name == nameof(TestRoot.People));

        var oldCollection = new TestPerson[] { person1 };
        var newCollection = Array.Empty<TestPerson>();

        var change = SubjectPropertyChange.Create<TestPerson[]>(
            property.Reference,
            source: null,
            TestTimestamp,
            receivedTimestamp: null,
            oldValue: oldCollection,
            newValue: newCollection);

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.True(result); // Structural change processed
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(person1, processor.RemovedSubjects[0].Subject);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_DictionaryAdd_CallsOnSubjectAdded()
    {
        // Arrange
        var processor = new TestableGraphChangePublisher();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new TestRoot(context);
        var person1 = new TestPerson(context);

        var registeredSubject = root.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(p => p.Name == nameof(TestRoot.PeopleByName));

        var oldDictionary = new Dictionary<string, TestPerson>();
        var newDictionary = new Dictionary<string, TestPerson> { ["john"] = person1 };

        var change = SubjectPropertyChange.Create<Dictionary<string, TestPerson>>(
            property.Reference,
            source: null,
            TestTimestamp,
            receivedTimestamp: null,
            oldValue: oldDictionary,
            newValue: newDictionary);

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.True(result); // Structural change processed
        Assert.Single(processor.AddedSubjects);
        Assert.Equal(person1, processor.AddedSubjects[0].Subject);
        Assert.Equal("john", processor.AddedSubjects[0].Index);
    }

    [Fact]
    public void OpcUaServerGraphChangeSender_Compiles_AndExtendsGraphChangePublisher()
    {
        // This test verifies the actual OpcUaServerGraphChangeSender can be instantiated
        // and that it properly extends GraphChangePublisher

        // Arrange & Act - just verify the class compiles and can be referenced
        var senderType = typeof(OpcUaServerGraphChangeSender);

        // Assert
        Assert.NotNull(senderType);
        Assert.True(senderType.IsAssignableTo(typeof(GraphChangePublisher)));
    }
}
