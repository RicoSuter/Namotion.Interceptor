using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Extensions;

public class SubjectChangeContextTests
{
    [Fact]
    public void WhenSetValueFromSource_ThenIsChangingFromSourceShouldReturnTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeObservable();
        
        var person = new Person(context);
        var propertyName = nameof(Person.FirstName);
        
        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));
        
        var source = Mock.Of<ISubjectConnector>();
        var propertyReference = new PropertyReference(person, propertyName);
        var registeredProperty = propertyReference.GetRegisteredProperty();
   
        // Act
        person.FirstName = "A";
        registeredProperty.SetValueFromConnector(source, null, "B");
        person.FirstName = "C";

        // Assert
        Assert.False(changes.ElementAt(0).Source == source);
        Assert.True(changes.ElementAt(1).Source == source);
        Assert.False(changes.ElementAt(2).Source == source);
    }
}