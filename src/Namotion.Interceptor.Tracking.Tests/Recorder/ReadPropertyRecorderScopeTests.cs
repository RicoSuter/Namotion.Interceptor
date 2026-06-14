using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Recorder;

public class ReadPropertyRecorderScopeTests
{
    [Fact]
    public void WhenPropertiesReadDuringScope_ThenRecordedPropertiesReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John", LastName = "Doe" };

        // Act
        using var scope = ReadPropertyRecorder.Start();
        _ = person.FirstName;
        _ = person.LastName;
        var properties = scope.GetPropertiesAndDispose();

        // Assert
        Assert.Equal(2, properties.Count);
        Assert.Contains(properties, p => p.Name == nameof(Person.FirstName));
        Assert.Contains(properties, p => p.Name == nameof(Person.LastName));
    }

    [Fact]
    public void WhenNoPropertiesRead_ThenEmptyCollectionReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        _ = new Person(context);

        // Act
        using var scope = ReadPropertyRecorder.Start();
        var properties = scope.GetPropertiesAndDispose();

        // Assert
        Assert.Empty(properties);
    }

    [Fact]
    public void WhenScopeDisposedTwice_ThenNoException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        _ = new Person(context);

        // Act & Assert
        var scope = ReadPropertyRecorder.Start();
        scope.Dispose();
        scope.Dispose();
    }

    [Fact]
    public void WhenPropertyReadAfterDispose_ThenNotRecorded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John", LastName = "Doe" };

        // Act
        var scope = ReadPropertyRecorder.Start();
        _ = person.FirstName;
        var properties = scope.GetPropertiesAndDispose();

        _ = person.LastName;

        // Assert
        Assert.Single(properties);
        Assert.Contains(properties, p => p.Name == nameof(Person.FirstName));
    }

    [Fact]
    public void WhenNestedScopesUsed_ThenEachRecordsIndependently()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John", LastName = "Doe" };

        // Act
        using var outerScope = ReadPropertyRecorder.Start();
        _ = person.FirstName;

        using var innerScope = ReadPropertyRecorder.Start();
        _ = person.LastName;
        var innerProperties = innerScope.GetPropertiesAndDispose();

        var outerProperties = outerScope.GetPropertiesAndDispose();

        // Assert
        Assert.Single(innerProperties);
        Assert.Contains(innerProperties, p => p.Name == nameof(Person.LastName));

        Assert.Equal(2, outerProperties.Count);
        Assert.Contains(outerProperties, p => p.Name == nameof(Person.FirstName));
        Assert.Contains(outerProperties, p => p.Name == nameof(Person.LastName));
    }

    [Fact]
    public void WhenSharedDictionaryProvided_ThenPropertiesAccumulateAcrossScopes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John", LastName = "Doe" };
        var sharedDict = new System.Collections.Concurrent.ConcurrentDictionary<PropertyReference, bool>();

        // Act
        using (var scope1 = ReadPropertyRecorder.Start(sharedDict))
        {
            _ = person.FirstName;
            scope1.GetPropertiesAndDispose();
        }

        using (var scope2 = ReadPropertyRecorder.Start(sharedDict))
        {
            _ = person.LastName;
            scope2.GetPropertiesAndDispose();
        }

        // Assert
        Assert.Equal(2, sharedDict.Count);
    }
}
