using System.Reactive.Concurrency;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Updates;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests;

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
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewChild3"),
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
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewChild3"),
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
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewPerson"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewMother"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, null, "Old", "NewChild3"),
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
            SubjectPropertyChange.Create(attribute2, null, DateTimeOffset.Now, null, 20, 21),
            SubjectPropertyChange.Create(attribute1, null, DateTimeOffset.Now, null, 10, 11),
            SubjectPropertyChange.Create(attribute1, null, DateTimeOffset.Now, null, 11, 12),
            SubjectPropertyChange.Create(attribute2, null, DateTimeOffset.Now, null, 20, 22),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
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
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
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
