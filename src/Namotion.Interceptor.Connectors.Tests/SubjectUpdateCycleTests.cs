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

    /// <summary>
    /// Tests the specific scenario where collection items reference each other.
    /// This can cause JSON cycles when the same SubjectUpdate object appears in
    /// multiple Insert operations.
    /// </summary>
    [Fact]
    public void WhenCollectionInsertHasItemsReferencingEachOther_ThenShouldNotFail()
    {
        // Arrange - Collection items that reference each other
        // This scenario causes the same SubjectUpdate to appear in multiple places
        // in the Operations list, which JSON.NET detects as a cycle
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };

        // Create items that reference each other (mutual cycle)
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        item1.Child = item2; // item1 -> item2
        item2.Child = item1; // item2 -> item1 (cycle between siblings)

        // Also make them reference parent
        item1.Parent = root;
        item2.Parent = root;

        // IMPORTANT: Add items to collection to register them, then create the change
        var oldItems = root.Items.ToList();
        root.Items = [item1, item2]; // This registers the items
        var newItems = root.Items.ToList();

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(root, "Items"),
                null,
                DateTimeOffset.UtcNow,
                null,
                oldItems,
                newItems),
        };

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);

        // Verify we have Operations with Inserts
        Assert.NotNull(partialUpdate.Properties["items"].Operations);
        Assert.Equal(2, partialUpdate.Properties["items"].Operations!.Count);
    }

    /// <summary>
    /// Tests adding a child that references its parent.
    /// The child's Insert operation should use a Reference for the parent, not the full SubjectUpdate.
    /// </summary>
    [Fact]
    public void WhenInsertedChildReferencesParent_ThenShouldCreateReference()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };

        // Create a child that references back to root
        var child = new CycleTestNode { Name = "Child" };
        child.Parent = root; // child -> root (cycle back to root)

        // IMPORTANT: Add child to collection to register it, then create the change
        var oldItems = root.Items.ToList();
        root.Items = [child]; // This registers the child
        var newItems = root.Items.ToList();

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(root, "Items"),
                null,
                DateTimeOffset.UtcNow,
                null,
                oldItems,
                newItems),
        };

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);

        // The child's parent property should be a reference to root, not the full root SubjectUpdate
        var operations = partialUpdate.Properties["items"].Operations;
        Assert.NotNull(operations);
        Assert.Single(operations);

        var insertedItem = operations[0].Item;
        Assert.NotNull(insertedItem);

        // The parent property in the inserted item should have a Reference (not Item with full content)
        // Note: JsonCamelCasePathProcessor converts to camelCase
        Assert.True(insertedItem.Properties.ContainsKey("parent"),
            $"Expected 'parent' key. Available keys: {string.Join(", ", insertedItem.Properties.Keys)}");
        var parentProperty = insertedItem.Properties["parent"];
        Assert.NotNull(parentProperty.Item);
        Assert.NotNull(parentProperty.Item!.Reference); // Should be a reference to root
    }

    /// <summary>
    /// Tests deeply nested collection inserts with cross-references.
    /// This reproduces the exact error path: .Properties.Item.Properties.Operations.Item...
    /// </summary>
    [Fact]
    public void WhenDeeplyNestedCollectionInsertsWithCrossReferences_ThenShouldNotFail()
    {
        // Arrange - Deeply nested structure matching error path:
        // $.Changes.Properties.Item.Properties.Item.Properties.Operations.Item...
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new CycleTestNode(context) { Name = "Root" };

        // Create nested levels with collections
        var level1 = new CycleTestNode { Name = "Level1" };
        var level2 = new CycleTestNode { Name = "Level2" };

        root.Child = level1;
        level1.Child = level2;
        level1.Parent = root;
        level2.Parent = level1;

        // Create items for level2's collection that reference back up the tree
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        item1.Parent = root; // references root (grandparent)
        item2.Parent = level1; // references level1 (parent)
        item1.Child = item2; // sibling reference
        item2.Child = item1; // sibling reference (cycle)

        // Items for level1's collection that also have nested items
        var level1Item = new CycleTestNode { Name = "Level1Item" };
        level1Item.Parent = root;

        // IMPORTANT: Add items to collections to register them
        var oldLevel2Items = level2.Items.ToList();
        level2.Items = [item1, item2];
        var newLevel2Items = level2.Items.ToList();

        // Update level1Item to include item1 AFTER item1 is registered
        level1Item.Items = [item1]; // nested collection

        var oldLevel1Items = level1.Items.ToList();
        level1.Items = [level1Item];
        var newLevel1Items = level1.Items.ToList();

        // Multiple changes at different levels
        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(level2, "Items"),
                null,
                DateTimeOffset.UtcNow,
                null,
                oldLevel2Items,
                newLevel2Items),
            SubjectPropertyChange.Create(
                new PropertyReference(level1, "Items"),
                null,
                DateTimeOffset.UtcNow,
                null,
                oldLevel1Items,
                newLevel1Items),
        };

        // Act
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert - verify JSON serialization doesn't fail due to cycles
        var json = JsonSerializer.Serialize(partialUpdate);
        Assert.NotNull(json);
    }
}
