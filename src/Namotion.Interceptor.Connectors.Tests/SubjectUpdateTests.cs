using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

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
        Assert.Equal(6, counter.TransformSubjectCount);
        Assert.Equal(36 /* 6x6 properties */ + 12 /* 6x2 attributes */, counter.TransformPropertyCount);

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
        Assert.Equal(5, counter.TransformSubjectCount);
        Assert.Equal(6, counter.TransformPropertyCount);

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
