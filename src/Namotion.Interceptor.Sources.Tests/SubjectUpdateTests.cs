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
            new SubjectPropertyChange(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, "Old", "NewPerson"),
            new SubjectPropertyChange(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, "Old", "NewFather"),
            new SubjectPropertyChange(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild1"),
            new SubjectPropertyChange(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild3"),
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
            new SubjectPropertyChange(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, "Old", "NewPerson"),
            new SubjectPropertyChange(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, "Old", "NewFather"),
            new SubjectPropertyChange(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild1"),
            new SubjectPropertyChange(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes, property =>
            {
                return property.Parent.Subject != child1; // ignore child1.FirstName
            }, (property, update) =>
            {

                if (property.Parent.Subject == child3)
                    update.Value = "TransformedValue"; // transform child3.FirstName

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
            new SubjectPropertyChange(new PropertyReference(person, "FirstName"), null, DateTimeOffset.Now, "Old", "NewPerson"), // ignored
            new SubjectPropertyChange(new PropertyReference(father, "FirstName"), null, DateTimeOffset.Now, "Old", "NewFather"),
            new SubjectPropertyChange(new PropertyReference(mother, "FirstName"), null, DateTimeOffset.Now, "Old", "NewMother"), // ignored
            new SubjectPropertyChange(new PropertyReference(child1, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild1"),
            new SubjectPropertyChange(new PropertyReference(child3, "FirstName"), null, DateTimeOffset.Now, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(father, changes) // TODO(perf): This method can probably made much faster in case of non-root subjects (no need to create many objects)
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
        context.GetPropertyChangedObservable().Subscribe(c => changes.Add(c));

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
}