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
            .WithPropertyChangeObservable();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        _ = new Person(context)
        {
            FirstName = "Rico",
            LastName = "Suter"
        };

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "NA" &&
            c.GetNewValue<string?>() == "Rico");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "Rico" &&
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
            .WithPropertyChangeObservable()
            .WithContextInheritance();
        
        // Act
        var car = new Car(context);
        
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
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
            .WithPropertyChangeObservable();

        var dateTime = DateTimeOffset.UtcNow.AddHours(-1);
        var person = new Person(context);
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
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
            // the fullname should contain the timestamp as firstname (trimmed, no trailing space)
            Assert.Equal($"{c.ChangedTimestamp}", c.GetNewValue<string>());
        }
    }

    [Fact]
    public void WhenNestedDerivedPropertiesChange_ThenAllLevelsAreRecalculated()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeObservable();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        changes.Clear();

        // Act - Change base property
        person.FirstName = "Jane";

        // Assert - All derived levels should update
        // Level 1: FullName depends on FirstName/LastName
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "John Doe" &&
            c.GetNewValue<string?>() == "Jane Doe");

        // Level 2: FullNameWithPrefix depends on FullName (nested derived)
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullNameWithPrefix) &&
            c.GetOldValue<string?>() == "Mr. John Doe" &&
            c.GetNewValue<string?>() == "Mr. Jane Doe");
    }

    [Fact]
    public void WhenDependenciesChange_ThenOldDependenciesAreCleanedUp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Get the FullName derived property reference
        var fullNameProp = new PropertyReference(person, nameof(Person.FullName));

        // Initially FullName depends on FirstName and LastName
        var initialDeps = fullNameProp.GetRequiredProperties();
        Assert.Equal(2, initialDeps.Count);

        // Act - Change FirstName which will re-record dependencies
        person.FirstName = "Jane";

        // Assert - Should still have 2 dependencies (FirstName, LastName)
        var afterDeps = fullNameProp.GetRequiredProperties();
        Assert.Equal(2, afterDeps.Count);
    }

    [Fact]
    public void WhenMultipleDerivedPropertiesDependOnSameSource_ThenAllAreUpdated()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeObservable();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        changes.Clear();

        // Act - Change FirstName (both FullName and FullNameWithPrefix depend on it)
        person.FirstName = "Jane";

        // Assert - Both derived properties should update
        Assert.Contains(changes, c => c.Property.Name == nameof(Person.FullName));
        Assert.Contains(changes, c => c.Property.Name == nameof(Person.FullNameWithPrefix));

        // FullName should update once
        Assert.Single(changes, c => c.Property.Name == nameof(Person.FullName));

        // FullNameWithPrefix should update once
        Assert.Single(changes, c => c.Property.Name == nameof(Person.FullNameWithPrefix));
    }

    [Fact]
    public void WhenSourcePropertyChangedMultipleTimes_ThenDerivedPropertyUpdatesCorrectly()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeObservable();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Person.FullName))
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        changes.Clear();

        // Act - Change FirstName multiple times rapidly
        person.FirstName = "Jane";
        person.FirstName = "Jack";
        person.FirstName = "Jim";

        // Assert
        Assert.Equal(3, changes.Count);
        Assert.Equal("Jane Doe", changes[0].GetNewValue<string?>());
        Assert.Equal("Jack Doe", changes[1].GetNewValue<string?>());
        Assert.Equal("Jim Doe", changes[2].GetNewValue<string?>());
    }

    [Fact]
    public void WhenDerivedPropertyAccessesMultipleObjects_ThenAllDependenciesAreTracked()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeObservable()
            .WithContextInheritance();

        var car = new Car(context);

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Car.AveragePressure))
            .Subscribe(changes.Add);

        // Act - Change pressure on different tires
        car.Tires[0].Pressure = 2.0m;
        car.Tires[1].Pressure = 2.2m;
        car.Tires[2].Pressure = 2.1m;
        car.Tires[3].Pressure = 2.3m;

        // Assert - AveragePressure should update for each tire change
        Assert.Equal(4, changes.Count);

        // Verify average is calculated correctly each time
        Assert.Equal((2.0m + 0 + 0 + 0) / 4, changes[0].GetNewValue<decimal>());
        Assert.Equal((2.0m + 2.2m + 0 + 0) / 4, changes[1].GetNewValue<decimal>());
        Assert.Equal((2.0m + 2.2m + 2.1m + 0) / 4, changes[2].GetNewValue<decimal>());
        Assert.Equal((2.0m + 2.2m + 2.1m + 2.3m) / 4, changes[3].GetNewValue<decimal>());
    }

    [Fact]
    public void WhenChangingPropertyWhichIsNotUsedInDerivedProperty_ThenNoRecalculationOccurs()
    {
        // Arrange
        var recalculationCount = 0;
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeObservable();
        
        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Person.FullName))
            .Subscribe(_ => recalculationCount++);

        // Act - Change an unrelated property
        person.Father = new Person();

        // Assert - No recalculation should occur
        Assert.Equal(0, recalculationCount);
    }

    [Fact]
    public void WhenBacklinkIsMaintained_ThenSourcePropertyKnowsItsUsedBy()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Act - Access derived property to establish dependencies
        _ = person.FullName;

        // Assert - FirstName should know it's used by FullName
        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var usedBy = firstNameProp.GetUsedByProperties();

        Assert.True(usedBy.Count > 0, "FirstName should have dependent properties");

        // Check that FullName is in the usedBy list
        var usedByArray = usedBy.Items.ToArray();
        Assert.Contains(usedByArray, p => p.Name == nameof(Person.FullName));
    }
    
    [Fact]
    public async Task WhenPropertyIsChanged_ThenTimestampIsCorrectInDerivedPropertyChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context)
        {
            FirstName = "Parent"
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        var date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (SubjectChangeContext.WithChangedTimestamp(date))
        {
            person.Mother = new Person // should trigger only mother property update itself (no new object properties)
            {
                FirstName = "Mother"
            };
            person.Mother.LastName = "MyMotherLN"; // should trigger FullName and FullNameWithPrefix updates
        }

        var update1 = changes
            .Select(c => $"{c.ChangedTimestamp:O}: {((Person)c.Property.Subject).FirstName}.{c.Property.Name} = {c.GetNewValue<object?>()}")
            .ToArray();

        changes.Clear();

        using (SubjectChangeContext.WithChangedTimestamp(date.AddSeconds(1)))
        {
            person.FirstName = "Parent-Updated"; // should trigger FullName and FullNameWithPrefix updates
            person.Mother = null;
        }

        var update2 = changes
            .Select(c => $"{c.ChangedTimestamp:O}: {((Person)c.Property.Subject).FirstName}.{c.Property.Name} = {c.GetNewValue<object?>()}")
            .ToArray();

        changes.Clear();

        // Assert
        await Verify(new
        {
            Update1 = update1,
            Update2 = update2,
        });
    }

    [Fact]
    public void WhenSourcePropertyChanges_ThenDerivedPropertyFiresPropertyChanged()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Jane";

        // Assert
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("FullName", firedEvents);
    }

    [Fact]
    public void WhenSourceChanges_ThenNestedDerivedPropertiesFirePropertyChanged()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Jane";

        // Assert - All levels should fire: FirstName -> FullName -> FullNameWithPrefix
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("FullName", firedEvents);
        Assert.Contains("FullNameWithPrefix", firedEvents);
    }

    [Fact]
    public void WhenSourceChanges_ThenAllDependentDerivedPropertiesFirePropertyChangedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Jane";

        // Assert - Each derived property should fire exactly once (no duplicates)
        Assert.Single(firedEvents, e => e == "FullName");
        Assert.Single(firedEvents, e => e == "FullNameWithPrefix");
    }
}