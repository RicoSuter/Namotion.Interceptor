using System.Collections.Concurrent;
using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests.Extensions;

public class SubjectMutationContextTests
{
    [Fact]
    public void WhenSetValueFromSource_ThenIsChangingFromSourceShouldReturnTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangedObservable();
        
        var person = new Person(context);
        var propertyName = nameof(Person.FirstName);
        
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangedObservable().Subscribe(c => changes.Add(c));
        
        var source = Mock.Of<ISubjectSource>();
        var propertyReference = new PropertyReference(person, propertyName);
        var registeredProperty = propertyReference.GetRegisteredProperty();
   
        // Act
        person.FirstName = "A";
        registeredProperty.SetValueFromSource(source, null, "B");
        person.FirstName = "C";

        // Assert
        Assert.False(changes.ElementAt(0).IsChangingFromSource(source));
        Assert.True(changes.ElementAt(1).IsChangingFromSource(source));
        Assert.False(changes.ElementAt(2).IsChangingFromSource(source));
    }
}