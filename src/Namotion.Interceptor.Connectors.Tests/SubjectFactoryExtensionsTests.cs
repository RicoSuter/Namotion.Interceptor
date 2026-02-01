using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectFactoryExtensionsTests
{
    [Fact]
    public void AppendSubjectsToCollection_WithArray_AppendsSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var factory = new DefaultSubjectFactory();
        var existing = new Person[] { new Person(context) };
        var toAdd = new Person(context);

        // Act
        var result = factory.AppendSubjectsToCollection(existing, toAdd);

        // Assert
        Assert.Equal(2, result.Count());
        Assert.Same(existing[0], result.First());
        Assert.Same(toAdd, result.Skip(1).First());
    }

    [Fact]
    public void AppendSubjectsToCollection_WithList_AppendsSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var factory = new DefaultSubjectFactory();
        var existing = new List<Person> { new Person(context) };
        var toAdd = new Person(context);

        // Act
        var result = factory.AppendSubjectsToCollection(existing, toAdd);

        // Assert
        Assert.Equal(2, result.Count());
        Assert.IsType<List<Person>>(result);
    }

    [Fact]
    public void RemoveSubjectsFromCollection_WithArray_RemovesAtIndices()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var factory = new DefaultSubjectFactory();
        var subject0 = new Person(context);
        var subject1 = new Person(context);
        var subject2 = new Person(context);
        var existing = new Person[] { subject0, subject1, subject2 };

        // Act
        var result = factory.RemoveSubjectsFromCollection(existing, 1);

        // Assert
        Assert.Equal(2, result.Count());
        Assert.Same(subject0, result.First());
        Assert.Same(subject2, result.Skip(1).First());
    }

    [Fact]
    public void AppendEntriesToDictionary_AddsSingleEntry()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var factory = new DefaultSubjectFactory();
        var subject1 = new Person(context);
        var subject2 = new Person(context);
        var existing = new Dictionary<string, Person> { ["key1"] = subject1 };

        // Act
        var result = factory.AppendEntriesToDictionary(
            existing,
            new KeyValuePair<object, IInterceptorSubject>("key2", subject2));

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Same(subject1, result["key1"]);
        Assert.Same(subject2, result["key2"]);
    }

    [Fact]
    public void RemoveEntriesFromDictionary_RemovesByKey()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var factory = new DefaultSubjectFactory();
        var subject1 = new Person(context);
        var subject2 = new Person(context);
        var existing = new Dictionary<string, Person>
        {
            ["key1"] = subject1,
            ["key2"] = subject2
        };

        // Act
        var result = factory.RemoveEntriesFromDictionary(existing, "key1");

        // Assert
        Assert.Single(result);
        Assert.True(result.ContainsKey("key2"));
        Assert.Same(subject2, result["key2"]);
    }
}
