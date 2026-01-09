using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for cycle detection and handling in SubjectUpdate serialization.
/// These tests ensure that circular object references don't cause infinite loops
/// or JSON serialization failures.
/// </summary>
public class SubjectUpdateCycleTests
{
    [Fact]
    public async Task WhenTreeHasLoop_ThenCreateCompleteShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Father = father,
        };
        father.Father = person; // create loop

        // Act
        var completeSubjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(person, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(completeSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenTreeHasLoop_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var person = new Person(context)
        {
            FirstName = "Child",
            Father = father,
        };
        father.Father = person; // create loop

        // The cycle occurs when the property change itself is a subject reference
        // that creates the cycle - changing Father property to a value that has
        // Father pointing back to person
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "Father"), null, DateTimeOffset.UtcNow, null, null, father),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
        Assert.Contains("Reference", json); // Should have a reference to break the cycle

        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public void WhenTreeHasLoopInParentPath_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange - cycle NOT involving the root, triggered via parent path traversal
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Create a cycle: father <-> mother (both point to each other)
        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        father.Mother = mother;
        mother.Father = father; // cycle between father and mother

        var person = new Person(context)
        {
            FirstName = "Child",
            Father = father,
            Mother = mother,
        };

        // Change both father's and mother's FirstName - this triggers parent path
        // traversal for both, which should NOT create object reference cycles
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewMother"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenChildPointsToParent_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange - child in collection points back to parent
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context) { FirstName = "Parent" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        person.Children = [child1, child2];
        child1.Father = person; // child points back to parent
        child2.Father = person; // child points back to parent

        // Change a child's property - parent path traversal should handle the cycle
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenCollectionChangesWithCycles_ThenCreatePartialUpdateShouldNotFail()
    {
        // Arrange - collection change where items have cycles
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context) { FirstName = "Parent" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        child1.Father = person; // child points to parent
        child2.Father = person;
        child1.Mother = child2; // siblings point to each other
        child2.Mother = child1;

        // Change the Children collection itself (not just a property of a child)
        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(person, "Children"),
                null,
                DateTimeOffset.UtcNow,
                null,
                new List<Person>(),
                new List<Person> { child1, child2 }),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenMultipleChangesCreateCycle_ThenShouldNotFail()
    {
        // Arrange - This is the key scenario that causes cycles:
        // Multiple changes where subjects reference each other
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context) { FirstName = "Person" };
        var sibling1 = new Person { FirstName = "Sibling1" };
        var sibling2 = new Person { FirstName = "Sibling2" };

        // Create mutual references (cycle)
        sibling1.Mother = sibling2;
        sibling2.Mother = sibling1;

        person.Father = sibling1;
        person.Mother = sibling2;

        // Multiple changes that each reference subjects in the cycle
        // When processing the second change, createReferenceUpdate:false
        // causes the actual SubjectUpdate object to be returned instead of a reference
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "Father"), null, DateTimeOffset.UtcNow, null, null, sibling1),
            SubjectPropertyChange.Create(new PropertyReference(person, "Mother"), null, DateTimeOffset.UtcNow, null, null, sibling2),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenDeepNestedStructureWithCycles_ThenShouldNotFail()
    {
        // Arrange - Deep nested structure with cycles similar to error path:
        // $.Changes.Properties.Item.Properties.Item.Properties.Collection.Item...
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };

        // Create a deep hierarchy with cycles
        var level1 = new Person { FirstName = "Level1" };
        var level2 = new Person { FirstName = "Level2" };
        var level3 = new Person { FirstName = "Level3" };

        root.Father = level1;
        level1.Father = level2;
        level2.Father = level3;
        level3.Father = root; // cycle back to root

        // Also add collection with cycles
        root.Children = [level1, level2, level3];
        level1.Children = [root]; // child points back to root

        // Multiple property changes across the hierarchy
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(level1, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewLevel1"),
            SubjectPropertyChange.Create(new PropertyReference(level2, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewLevel2"),
            SubjectPropertyChange.Create(new PropertyReference(level3, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewLevel3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenChildChangeReferencesParentAndParentHasCollection_ThenShouldNotFail()
    {
        // Arrange - This scenario causes cycles:
        // Root.Children = [Child]
        // Child.Father = Root
        // Change: Child.Father = Root (setting the parent reference)
        // This creates: Root.Children.Item -> Child, Child.Father.Item -> Root (CYCLE!)
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };

        root.Children = [child];
        child.Father = root; // child points back to root

        // Change the Father property to root - this should trigger cycle detection
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(child, "Father"), null, DateTimeOffset.UtcNow, null, null, root),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);
    }

    [Fact]
    public void WhenMultipleValueChangesOnNestedSubject_ThenSubjectUpdatesAreReused()
    {
        // Arrange - Multiple value changes on the same nested subject should
        // accumulate on the same SubjectUpdate, not create references
        // foo.Bar.Baz.A = 5
        // foo.Bar.Baz.B = 10
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var bar = new Person { FirstName = "Bar" };
        var baz = new Person { FirstName = "Baz" };

        root.Father = bar;
        bar.Father = baz;

        // Multiple value changes on the same nested subject (baz)
        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(baz, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewFirst"),
            SubjectPropertyChange.Create(new PropertyReference(baz, "LastName"), null, DateTimeOffset.UtcNow, null, "Old", "NewLast"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        // 1. The update should be serializable (no cycles)
        var json = JsonSerializer.Serialize(partialSubjectUpdate);
        Assert.NotNull(json);

        // 2. baz should have BOTH properties (accumulated, not referenced)
        var barUpdate = partialSubjectUpdate.Properties["father"];
        Assert.NotNull(barUpdate.Item);
        var bazUpdate = barUpdate.Item!.Properties["father"];
        Assert.NotNull(bazUpdate.Item);

        // baz's SubjectUpdate should have both FirstName and LastName
        Assert.True(bazUpdate.Item!.Properties.ContainsKey("firstName"));
        Assert.True(bazUpdate.Item!.Properties.ContainsKey("lastName"));
        Assert.Equal("NewFirst", bazUpdate.Item!.Properties["firstName"].Value);
        Assert.Equal("NewLast", bazUpdate.Item!.Properties["lastName"].Value);

        // baz should NOT be a reference (should have Properties, not just Reference)
        Assert.Null(bazUpdate.Item!.Reference);
    }
    
    /// <summary>
    /// Creates a comprehensive model with refs, collections, dictionaries, and multiple cycles.
    /// Tests CreateCompleteUpdate handles all these correctly.
    /// </summary>
    [Fact]
    public async Task WhenModelHasRefsCollectionsDictionariesWithCycles_ThenCreateCompleteUpdateSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Build the model structure:
        // root
        // ├── Self -> root (direct self-cycle)
        // ├── Child -> child1
        // │   ├── Parent -> root (cycle back)
        // │   ├── Items[0] -> grandChild
        // │   │   └── Parent -> root (deep cycle)
        // │   └── Lookup["key"] -> anotherChild
        // │       └── Parent -> root (dict cycle)
        // └── Items[0] -> child2
        //     └── Parent -> root (collection cycle)

        var root = new CycleTestNode(context) { Name = "Root" };
        var child1 = new CycleTestNode { Name = "Child1" };
        var grandChild = new CycleTestNode { Name = "GrandChild" };
        var anotherChild = new CycleTestNode { Name = "AnotherChild" };
        var child2 = new CycleTestNode { Name = "Child2" };

        // Set up refs with cycles
        root.Self = root; // direct self-cycle
        root.Child = child1;
        child1.Parent = root; // cycle back to root

        // Set up collection with cycles
        child1.Items = [grandChild];
        grandChild.Parent = root; // deep cycle back to root
        root.Items = [child2];
        child2.Parent = root; // collection item cycles back

        // Set up dictionary with cycles
        child1.Lookup = new Dictionary<string, CycleTestNode>
        {
            ["key"] = anotherChild
        };
        anotherChild.Parent = root; // dict value cycles back

        // Act
        var completeUpdate = SubjectUpdate
            .CreateCompleteUpdate(root, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(completeUpdate);
        Assert.NotNull(json);
        Assert.Contains("Reference", json); // Should have references to break cycles

        await Verify(completeUpdate).DisableDateCounting();
    }

    /// <summary>
    /// Creates a comprehensive model and makes changes to all property types.
    /// Tests CreatePartialUpdateFromChanges handles cycles correctly with:
    /// - Multiple changes per property (last wins)
    /// - Reference reassignments
    /// - Collection changes
    /// - Dictionary changes
    /// - Attribute changes
    /// </summary>
    [Fact]
    public async Task WhenModelHasRefsCollectionsDictionariesWithCycles_ThenCreatePartialUpdateSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable()
            .WithRegistry();

        // Build initial model with cycles (same as above)
        var root = new CycleTestNode(context) { Name = "Root" };
        var child1 = new CycleTestNode { Name = "Child1" };
        var grandChild = new CycleTestNode { Name = "GrandChild" };
        var anotherChild = new CycleTestNode { Name = "AnotherChild" };
        var child2 = new CycleTestNode { Name = "Child2" };

        root.Self = root;
        root.Child = child1;
        child1.Parent = root;
        child1.Items = [grandChild];
        grandChild.Parent = root;
        root.Items = [child2];
        child2.Parent = root;
        child1.Lookup = new Dictionary<string, CycleTestNode> { ["key"] = anotherChild };
        anotherChild.Parent = root;

        // Track changes
        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act - Make changes to all property types, some multiple times (last wins)

        // Value property changes (multiple times - last wins)
        root.Name = "Root_v1";
        root.Name = "Root_v2";
        root.Name = "Root_Final";

        child1.Name = "Child1_v1";
        child1.Name = "Child1_Final";

        grandChild.Name = "GrandChild_Final";
        anotherChild.Name = "AnotherChild_Final";
        child2.Name = "Child2_Final";

        // Attribute changes
        root.Name_Status = "status_v1";
        root.Name_Status = "status_Final";
        child1.Name_Status = "child1_status";

        // Reference reassignments
        var newChild = new CycleTestNode { Name = "NewChild" };
        newChild.Parent = root; // new child also cycles back
        root.Child = newChild; // reassign ref

        // Self-reference change
        root.Self = child1; // change self-ref to point to child1 instead
        child1.Self = root; // and child1 points back (cycle)

        // Collection changes
        var newItem = new CycleTestNode { Name = "NewItem" };
        newItem.Parent = root; // cycles back
        root.Items = [child2, newItem]; // add new item

        // Dictionary changes
        var dictItem = new CycleTestNode { Name = "DictItem" };
        dictItem.Parent = root; // cycles back
        child1.Lookup = new Dictionary<string, CycleTestNode>
        {
            ["key"] = anotherChild, // keep existing
            ["newKey"] = dictItem   // add new entry
        };

        // Create partial update
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);

        // Verify "last wins" behavior for root.Name
        Assert.True(partialUpdate.Properties.ContainsKey("name"));
        Assert.Equal("Root_Final", partialUpdate.Properties["name"].Value);

        await Verify(partialUpdate).DisableDateCounting();
    }
}
