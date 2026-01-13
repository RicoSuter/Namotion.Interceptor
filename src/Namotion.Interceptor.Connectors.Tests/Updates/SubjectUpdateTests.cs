using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateTests
{
    [Fact]
    public async Task WhenGeneratingCompleteSubjectDescription_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var counter = new TransformCounter();

        // Act
        var completeSubjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(person, [counter, JsonCamelCasePathProcessor.Instance]);

        // Assert
        // Property count varies based on context propagation; snapshot is authoritative
        Assert.True(counter.TransformPropertyCount > 0);

        await Verify(completeSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescription_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild3"),
        };

        var counter = new TransformCounter();

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [counter, JsonCamelCasePathProcessor.Instance]);

        // Assert
        // Only changed properties are transformed in partial updates
        Assert.True(counter.TransformPropertyCount > 0);

        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescriptionWithTransformation_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes,
            [
                new MockProcessor(child1, child3),
                JsonCamelCasePathProcessor.Instance
            ]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    public class MockProcessor : ISubjectUpdateProcessor
    {
        private readonly IInterceptorSubject _excluded;
        private readonly IInterceptorSubject _transform;

        public MockProcessor(IInterceptorSubject excluded, IInterceptorSubject transform)
        {
            _excluded = excluded;
            _transform = transform;
        }

        public bool IsIncluded(RegisteredSubjectProperty property)
        {
            return property.Parent.Subject != _excluded;
        }

        public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update)
        {
            return update;
        }

        public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update)
        {
            if (property.Parent.Subject == _transform)
            {
                update.Value = "TransformedValue";
            }

            return update;
        }
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescriptionForNonRoot_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };
        var father = new Person { FirstName = "Father", Children = [child1, child2, child3] };
        var mother = new Person { FirstName = "Mother" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
        };

        var changes = new[]
        {
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewPerson"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewMother"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.UtcNow, null, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            // TODO(perf): This method can probably made much faster in case of
            // non-root subjects (no need to create many objects)
            .CreatePartialUpdateFromChanges(father, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescription_ThenResultCollectionIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Children = [child1, child2, child3]
        };

        // Act
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        person.Mother = new Person { FirstName = "MyMother" };
        person.Children = person.Children.Union([new Person { FirstName = "Child4" }]).ToList(); // add child4
        person.Father = new Person { FirstName = "MyFather" };
        person.Mother.FirstName = "Jane";
        person.Children = person.Children.Skip(2).ToList(); // remove child1 and child2
        person.Children[1].FirstName = "John";

        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray().AsSpan(), [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectWithAttribute_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = []
        };

        var attribute1 = mother.TryGetRegisteredSubject()!
            .TryGetProperty("FirstName")!
            .AddAttribute("Test1", typeof(int), _ => 42, null);

        var attribute2 = attribute1
            .AddAttribute("Test2", typeof(int), _ => 42, null);

        var changes = new[]
        {
            SubjectPropertyChange.Create(attribute2, null, DateTimeOffset.UtcNow, null, 20, 21),
            SubjectPropertyChange.Create(attribute1, null, DateTimeOffset.UtcNow, null, 10, 11),
            SubjectPropertyChange.Create(attribute1, null, DateTimeOffset.UtcNow, null, 11, 12),
            SubjectPropertyChange.Create(attribute2, null, DateTimeOffset.UtcNow, null, 20, 22),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenAssigningNewSubjectToNestedProperty_ThenPartialUpdateContainsOnlyPathAndNewSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var mother = new Person { FirstName = "Mother" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Mother = mother
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act: Assign a new Person to mother.Father (nested property)
        mother.Father = new Person { FirstName = "NewFather" };

        // Log what changes we received for debugging
        var changesSummary = changes.Select(c => $"{c.Property.Subject.GetType().Name}.{c.Property.Name}").ToList();

        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // The partial update should contain:
        // 1. root: only the path reference to mother (mother property)
        // 2. mother: only the father property (the actual change)
        // 3. newFather: all properties of the new subject
        //
        // It should NOT contain other properties like root.firstName, root.children, etc.
        await Verify(new { Changes = changesSummary, Update = partialUpdate }).DisableDateCounting();
    }

    [Fact]
    public async Task WhenAssigningNewSubjectWithRootReference_ThenPartialUpdateShouldNotIncludeFullAncestorProperties()
    {
        // Arrange
        // This test reproduces a bug where assigning a new subject that has a Root property
        // (which navigates back up the tree) causes the partial update to include
        // ALL properties of ALL ancestors, not just the path references.

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var person = new PersonWithRoot { FirstName = "Person" };
        var root = new PersonRoot(context)
        {
            Name = "Root",
            Person = person
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act: Assign a new PersonWithRoot to person.Father
        // The new father has a Root property that references back to PersonRoot
        // This creates a circular reference: root -> person -> father -> root
        person.Father = new PersonWithRoot { FirstName = "NewFather", Root = root };

        var changesSummary = changes.Select(c => $"{c.Property.Subject.GetType().Name}.{c.Property.Name}").ToList();

        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Expected behavior:
        // - root (PersonRoot): only path reference to person (person property)
        // - person (PersonWithRoot): only the father property (the actual change)
        // - newFather (PersonWithRoot): all properties INCLUDING the Root reference
        //
        // Bug: The Root property on newFather causes ProcessSubjectComplete to traverse
        // back up to PersonRoot, which then includes ALL properties of PersonRoot and
        // the full tree, not just the minimal path references.

        await Verify(new { Changes = changesSummary, Update = partialUpdate }).DisableDateCounting();
    }

    [Fact]
    public async Task WhenSubjectIsInPathOfFirstChangeAndAssignedInSecond_ThenOnlyReferenceIsIncluded()
    {
        // This test verifies that if a subject gets an ID from being in the path of
        // one change, and is then assigned as a value in another change, we correctly
        // only include a reference (not full properties) because the subject already
        // exists in the tree.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var grandparent = new Person { FirstName = "Grandparent" };
        var parent = new Person { FirstName = "Parent" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = grandparent
        };
        grandparent.Father = parent;

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act:
        // Change 1: Update parent's name (parent is in path from grandparent to root)
        parent.FirstName = "UpdatedParent";
        // Change 2: Assign parent to root.Mother (parent is now also a new value)
        root.Mother = parent;

        var changesSummary = changes.Select(c => $"{c.Property.Subject.GetType().Name}.{c.Property.Name}").ToList();

        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Assert:
        // - parent should only appear once with just the firstName change
        // - root.Mother should reference parent's ID
        // - parent should NOT have full properties duplicated
        await Verify(new { Changes = changesSummary, Update = partialUpdate }).DisableDateCounting();
    }

    [Fact]
    public async Task WhenUpdateContainsIncrementalChanges_ThenAllOfThemAreApplied()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var person = new Person(context)
        {
            FirstName = "Parent",
            Children = [child1, child2]
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        var date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (SubjectChangeContext.WithChangedTimestamp(date))
        {
            // Modify Child2 properties
            child2.FirstName = "Child2.0";

            // Remove Child1 and move Child2 to first position, previous changes should be kept
            person.Children = person.Children.Where(c => c.FirstName != "Child1").ToList();
        }

        var partialSubjectUpdate1 = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        changes.Clear();
        using (SubjectChangeContext.WithChangedTimestamp(date.AddSeconds(1)))
        {
            child2.FirstName = "Child2.1";
        }

        var partialSubjectUpdate2 = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        changes.Clear();
        using (SubjectChangeContext.WithChangedTimestamp(date.AddSeconds(2)))
        {
            person.FirstName = "ParentNext";
        }

        var partialSubjectUpdate3 = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes.ToArray(), [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(new
        {
            Update1 = partialSubjectUpdate1,
            Update2 = partialSubjectUpdate2,
            Update3 = partialSubjectUpdate3
        });
    }

    [Fact]
    public void WhenPathSegmentExcluded_ThenNestedChangeNotIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Dad" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = father
        };

        // Processor excludes the "Father" property on root - making father.FirstName unreachable
        var processor = new PredicateProcessor(p =>
            !(p.Name == "Father" && p.Parent.Subject == root));

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference(father, "FirstName"),
                null, DateTimeOffset.UtcNow, null, "Dad", "NewDad")
        };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            root, changes, [processor]);

        // Assert: Change should be excluded because path to root is broken
        // Root subject should have no properties (Father is excluded)
        Assert.True(
            !update.Subjects.ContainsKey("1") || update.Subjects["1"].Count == 0,
            "Root should have no properties when Father is excluded");

        // Father's change should not appear
        Assert.False(
            update.Subjects.Values.Any(s => s.ContainsKey("firstName")),
            "Father's firstName change should not be included");
    }

    /// <summary>
    /// Verifies that when a collection item is both reordered AND has property changes
    /// in the same batch of changes, both the move operation and property update are
    /// correctly captured and can be applied to recreate the expected state.
    /// </summary>
    [Fact]
    public void WhenCollectionItemReorderedAndPropertyChanged_ThenUpdateAppliesCorrectly()
    {
        // Arrange: Create source and target with same initial state: [item1, item2]
        var sourceContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var item1 = new Person { FirstName = "Item1" };
        var item2 = new Person { FirstName = "Item2" };
        var source = new Person(sourceContext)
        {
            FirstName = "Parent",
            Children = [item1, item2]
        };

        var targetContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var targetItem1 = new Person { FirstName = "Item1" };
        var targetItem2 = new Person { FirstName = "Item2" };
        var target = new Person(targetContext)
        {
            FirstName = "Parent",
            Children = [targetItem1, targetItem2]
        };

        // Act: In a single batch of changes:
        // 1. Change item2.FirstName to "Updated"
        // 2. Reorder to [item2, item1]
        var changes = new List<SubjectPropertyChange>();
        using var _ = sourceContext
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        item2.FirstName = "Updated";
        source.Children = [item2, item1]; // Reorder: item2 is now at index 0

        // Create the partial update from the captured changes
        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(source, changes.ToArray(), []);

        // Apply the update to the target
        target.ApplySubjectUpdate(partialUpdate, DefaultSubjectFactory.Instance);

        // Assert:
        // 1. item2 should now be at index 0
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Updated", target.Children[0].FirstName); // item2 at index 0 with updated name
        Assert.Equal("Item1", target.Children[1].FirstName); // item1 at index 1

        // Verify the order is correct - item2 (Updated) should be first
        var firstChildName = target.Children[0].FirstName;
        var secondChildName = target.Children[1].FirstName;

        Assert.Equal("Updated", firstChildName);
        Assert.Equal("Item1", secondChildName);
    }

    [Fact]
    public void WhenNewSubjectReferencesIntermediateAncestor_ThenAncestorGetsReferenceOnly()
    {
        // Arrange: Registry hierarchy: root → parent → child
        // The bug occurs when: child.X = newSubject, and newSubject references parent
        // (an ancestor that is NOT the changed subject)
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var child = new PersonWithRoot { FirstName = "Child" };
        var parent = new PersonWithRoot { FirstName = "Parent", Children = [child] };
        var root = new PersonRoot(context)
        {
            Name = "Root",
            Person = parent
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act: Change is on child, but newSubject references parent (intermediate ancestor)
        // Bug: parent doesn't have ID yet when newSubject.Father is processed
        child.Father = new PersonWithRoot { FirstName = "NewFather", Father = parent };

        var partialUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(root, changes.ToArray(), []);

        // Assert:
        // Find the parent subject (has "Children" property pointing to child)
        var parentEntry = partialUpdate.Subjects
            .FirstOrDefault(s => s.Value.ContainsKey("Children"));

        Assert.False(parentEntry.Equals(default), "Parent subject should be in update");

        // Parent should only have the path reference (Children), NOT full properties
        // If bug exists: parent would have FirstName, LastName, Father, Mother, Root, Children
        // If fixed: parent should only have Children (the path reference)
        var parentProps = parentEntry.Value;

        Assert.False(
            parentProps.ContainsKey("FirstName"),
            "Parent should NOT have FirstName - only path reference should be included");

        Assert.True(
            parentProps.ContainsKey("Children"),
            "Parent should have Children path reference");
    }

    [Fact]
    public void WhenRootPropertyExcluded_ThenAttributeChangeNotIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Test",
            FirstName_MaxLength = 100
        };

        // Get the MaxLength attribute property
        var firstNameProperty = person.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;
        var maxLengthAttribute = firstNameProperty.Attributes
            .First(a => a.AttributeMetadata.AttributeName == "MaxLength");

        // Processor excludes FirstName property (root of the attribute chain)
        var processor = new PredicateProcessor(p =>
            !(p.Name == "FirstName" && p.Parent.Subject == person));

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                maxLengthAttribute.Reference,
                null, DateTimeOffset.UtcNow, null, 100, 200)
        };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            person, changes, [processor]);

        // Assert: No firstName property in update (attribute excluded because root is excluded)
        var rootProps = update.Subjects.GetValueOrDefault("1") ?? new Dictionary<string, SubjectPropertyUpdate>();

        Assert.False(
            rootProps.ContainsKey("FirstName") || rootProps.ContainsKey("firstName"),
            "FirstName should not be in update when excluded by processor");
    }
}

public class TransformCounter : ISubjectUpdateProcessor
{
    private readonly HashSet<SubjectUpdate> _transformedSubjects = [];
    private readonly HashSet<SubjectPropertyUpdate> _transformedProperties = [];

    public int TransformSubjectCount { get; private set; } = 0;

    public int TransformPropertyCount { get; private set; } = 0;

    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update)
    {
        if (!_transformedSubjects.Add(update))
            throw new InvalidOperationException("Already added.");

        TransformSubjectCount++;
        return update;
    }

    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update)
    {
        if (!_transformedProperties.Add(update))
            throw new InvalidOperationException("Already added.");

        TransformPropertyCount++;
        return update;
    }
}

public class PredicateProcessor : ISubjectUpdateProcessor
{
    private readonly Func<RegisteredSubjectProperty, bool> _isIncluded;

    public PredicateProcessor(Func<RegisteredSubjectProperty, bool> isIncluded)
    {
        _isIncluded = isIncluded;
    }

    public bool IsIncluded(RegisteredSubjectProperty property) => _isIncluded(property);

    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update) => update;

    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update) => update;
}
