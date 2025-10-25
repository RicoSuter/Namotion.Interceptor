using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyChangeHandlerTests
{
    [Fact]
    public void WhenChangingPropertyWhichIsUsedInDerivedProperty_ThenDerivedPropertyIsChanged()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangedObservable();

        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        var person = new Person(context)
        {
            FirstName = "Rico",
            LastName = "Suter"
        };

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == " " &&
            c.GetNewValue<string?>() == "Rico ");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "Rico " &&
            c.GetNewValue<string?>() == "Rico Suter");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullNameWithPrefix) &&
            c.GetNewValue<string?>() == "Mr. Rico Suter");
    }

    [Fact]
    public void WhenTrackingDerivedPropertiesUsingPropertiesFromOtherSubjectsAndInheritance_ThenChangesAreTracked()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangedObservable()
            .WithContextInheritance();
        
        // Act
        var car = new Car(context);
        
        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);
        
        car.Tires[0].Pressure = 2.0m;       
        
        // Assert
        Assert.Contains(changes, c => c.Property.Name == "AveragePressure");
    }

    [Fact]
    public void WhenDerivedPropertyChanges_ThenTimestampIsConsistentWithMutationContext()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangedObservable();

        var dateTime = DateTimeOffset.UtcNow.AddHours(-1);
        var person = new Person(context);
        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Person.FullName))
            .Subscribe(changes.Add);

        // Act
        for (var i = 0; i < 10000; i++)
        {
            var dt = dateTime.AddSeconds(i);
            using (SubjectChangeContext.WithChangedTimestamp(dt))
            {
                person.FirstName = dt.ToString();
            }
        }

        // Assert
        Assert.Equal(10000, changes.Count);
        foreach (var c in changes)
        {
            // the fullname should contain the timestamp as firstname
            Assert.Equal(c.GetNewValue<string>(), $"{c.ChangedTimestamp} "); 
        }
    }
}