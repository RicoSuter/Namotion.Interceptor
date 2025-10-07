using System.Reactive.Concurrency;
using Namotion.Interceptor.Registry;
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

        // Act
        var completeSubjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(person)
            .ConvertToJsonCamelCasePath();

        // Assert
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
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes)
            .ConvertToJsonCamelCasePath();

        // Assert
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
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, "Old", "NewPerson"),
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, property =>
            {
                return property.Parent.Subject != child1; // exclude child1.FirstName
            }, (property, update) =>
            {
                if (property.Parent.Subject == child3)
                {
                    update.Value = "TransformedValue"; // transform child3.FirstName
                }

                return update;
            })
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
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
            SubjectPropertyChange.Create(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, "Old", "NewPerson"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, "Old", "NewFather"),
            SubjectPropertyChange.Create(new PropertyReference(mother, "FirstName"), null, DateTimeOffset.Now, "Old", "NewMother"), // ignored
            SubjectPropertyChange.Create(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild1"),
            SubjectPropertyChange.Create(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            // TODO(perf): This method can probably made much faster in case of
            // non-root subjects (no need to create many objects)
            .CreatePartialUpdateFromChanges(father, changes) 
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescription_ThenResultCollectionIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable()
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
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        person.Mother = new Person { FirstName = "MyMother" };
        person.Children = person.Children.Union([new Person { FirstName = "Child4" }]).ToList(); // add child4
        person.Father = new Person { FirstName = "MyFather" };
        person.Mother.FirstName = "Jane";
        person.Children = person.Children.Skip(2).ToList(); // remove child1 and child2
        person.Children[1].FirstName = "John";

        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes)
            .ConvertToJsonCamelCasePath();

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
            SubjectPropertyChange.Create(attribute2, null, DateTimeOffset.Now, 20, 21),
            SubjectPropertyChange.Create(attribute1, null, DateTimeOffset.Now, 10, 11),
            SubjectPropertyChange.Create(attribute1, null, DateTimeOffset.Now, 11, 12),
            SubjectPropertyChange.Create(attribute2, null, DateTimeOffset.Now, 20, 22),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes)
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(partialSubjectUpdate).DisableDateCounting();
    }
}
