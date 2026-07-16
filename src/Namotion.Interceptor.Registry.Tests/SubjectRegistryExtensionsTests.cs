using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryExtensionsTests
{
    [Fact]
    public void WhenResolvingRegisteredProperty_ThenItIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p.Mother!.FirstName);
        var registeredSubject = person.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.NotNull(registeredSubject);
        Assert.Equal(person.Mother, registeredSubjectProperty.Subject);

        Assert.Equal("Mother", registeredSubjectProperty.GetValue());
    }

    [Fact]
    public void WhenExpressionSelectsValueTypedLeafAtRoot_ThenPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Child"
        };

        // Act
        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p.FirstName_MaxLength);

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.Equal("FirstName_MaxLength", registeredSubjectProperty.Name);
    }

    [Fact]
    public void WhenExpressionSelectsSameTypeNestedValueTypedLeaf_ThenPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother"
            }
        };

        // Act
        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p.Mother!.FirstName_MaxLength);

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.Equal("FirstName_MaxLength", registeredSubjectProperty.Name);
        Assert.Equal(person.Mother, registeredSubjectProperty.Subject);
    }

    [Fact]
    public void WhenExpressionSelectsCrossTypeNestedValueTypedLeaf_ThenPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var room = new Room(context)
        {
            Light = new Light
            {
                Name = "Ceiling",
                On = true
            }
        };

        // Act
        var registeredSubjectProperty = room.TryGetRegisteredProperty(r => r.Light!.On);

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.Equal("On", registeredSubjectProperty.Name);
        Assert.Equal(room.Light, registeredSubjectProperty.Subject);
    }

    [Fact]
    public void WhenIntermediateSubjectIsNull_ThenPropertyIsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Child"
        };

        // Act
        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p.Mother!.FirstName);

        // Assert
        Assert.Null(registeredSubjectProperty);
    }

    [Fact]
    public void WhenExpressionIsNotMemberAccess_ThenPropertyIsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Child"
        };

        // Act
        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p);

        // Assert
        Assert.Null(registeredSubjectProperty);
    }

    [Fact]
    public void WhenNonAdjacentIntermediateAtDepthThreeIsNull_ThenPropertyIsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Room is attached, but Room.Zone (the first hop of the depth-3 chain) is never
        // set, so Zone.Light.On can never be reached — the resolver must not dereference
        // through the null Zone to get there.
        var room = new Room(context);

        // Act
        var registeredSubjectProperty = room.TryGetRegisteredProperty(r => r.Zone!.Light!.On);

        // Assert
        Assert.Null(registeredSubjectProperty);
    }

    [Fact]
    public void WhenDepthThreeChainIsFullyAttached_ThenDeepestPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var room = new Room(context)
        {
            Zone = new Zone
            {
                Light = new Light
                {
                    Name = "Ceiling",
                    On = true
                }
            }
        };

        // Act
        var registeredSubjectProperty = room.TryGetRegisteredProperty(r => r.Zone!.Light!.On);

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.Equal("On", registeredSubjectProperty.Name);
        Assert.Equal(room.Zone.Light, registeredSubjectProperty.Subject);
    }

    [Fact]
    public void WhenExpressionSelectsLeafThroughCollectionIndex_ThenPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Children[1] is an indexer (not a member access), so the resolver must evaluate
        // the segment against the actual graph rather than walking it through the registry.
        var person = new Person(context)
        {
            FirstName = "Root",
            Children =
            [
                new Person { FirstName = "A" },
                new Person { FirstName = "B" }
            ]
        };

        // Act
        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p.Children[1].FirstName);

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.Equal("FirstName", registeredSubjectProperty.Name);
        Assert.Equal(person.Children[1], registeredSubjectProperty.Subject);
    }

    [Fact]
    public void WhenValueTypedLeafIsBoxedByObjectTypedExpression_ThenPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Child"
        };

        // Explicit object? value type forces the int leaf into a boxing Convert node,
        // exercising the conversion-unwrap loop that keeps Expression<Func<T, object?>>
        // selectors working.
        // Act
        var registeredSubjectProperty = person.TryGetRegisteredProperty<Person, object?>(p => p.FirstName_MaxLength);

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.Equal("FirstName_MaxLength", registeredSubjectProperty.Name);
    }
}