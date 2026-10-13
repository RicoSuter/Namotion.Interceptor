using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Inputs;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectPathSelectionTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string propertyName)
    {
        return subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
    }

    [Fact]
    public void WhenPathLeadsToAPropertyAndThePropertyHoldsAValue_ThenThePropertyCanBeSelected()
    {
        // Arrange
        var card = new Card(_context) { Title = "Pump" };
        var property = GetProperty(card, nameof(Card.Title));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, subjectType: null);

        // Assert
        Assert.True(canSelect);
    }

    [Fact]
    public void WhenPathLeadsToAPropertyAndThePropertyHoldsSubjects_ThenThePropertyCannotBeSelected()
    {
        // Arrange
        var map = new ValueMap(_context) { Cases = [new ValueMapCase(_context)] };
        var card = new Card(_context) { Content = map };
        var reference = GetProperty(card, nameof(Card.Content));
        var emptyReference = GetProperty(card, nameof(Card.Footer));
        var collection = GetProperty(map, nameof(ValueMap.Cases));

        // Act
        var canSelectReference = SubjectPathSelection.CanSelectProperty(reference, subjectType: null);
        var canSelectEmptyReference = SubjectPathSelection.CanSelectProperty(emptyReference, subjectType: null);
        var canSelectCollection = SubjectPathSelection.CanSelectProperty(collection, subjectType: null);

        // Assert
        Assert.False(canSelectReference);
        Assert.False(canSelectEmptyReference);
        Assert.False(canSelectCollection);
    }

    [Fact]
    public void WhenPathLeadsToAProperty_ThenNoSubjectCanBeSelected()
    {
        // Arrange
        var map = new ValueMap(_context);
        var card = new Card(_context);

        // Act
        var canSelectMap = SubjectPathSelection.CanSelectSubject(map, subjectType: null);
        var canSelectCard = SubjectPathSelection.CanSelectSubject(card, subjectType: null);

        // Assert
        Assert.False(canSelectMap);
        Assert.False(canSelectCard);
    }

    [Fact]
    public void WhenPathLeadsToASubjectOfAType_ThenOnlyASubjectOfThatTypeCanBeSelected()
    {
        // Arrange
        var map = new ValueMap(_context);
        var card = new Card(_context);

        // Act
        var canSelectMap = SubjectPathSelection.CanSelectSubject(map, typeof(ValueMap));
        var canSelectCard = SubjectPathSelection.CanSelectSubject(card, typeof(ValueMap));

        // Assert
        Assert.True(canSelectMap);
        Assert.False(canSelectCard);
    }

    [Fact]
    public void WhenPathLeadsToASubjectOfAnInterface_ThenASubjectThatImplementsItCanBeSelected()
    {
        // Arrange
        var map = new ValueMap(_context);
        var mapCase = new ValueMapCase(_context);

        // Act
        var canSelectMap = SubjectPathSelection.CanSelectSubject(map, typeof(ITitleProvider));
        var canSelectCase = SubjectPathSelection.CanSelectSubject(mapCase, typeof(ITitleProvider));

        // Assert
        Assert.True(canSelectMap);
        Assert.False(canSelectCase);
    }

    [Fact]
    public void WhenAReferenceLeadsToASubjectOfTheType_ThenThePropertyCanBeSelected()
    {
        // Arrange
        var card = new Card(_context) { Content = new ValueMap(_context) };
        var property = GetProperty(card, nameof(Card.Content));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, typeof(ValueMap));

        // Assert
        Assert.True(canSelect);
    }

    [Fact]
    public void WhenAReferenceLeadsToASubjectOfAnotherType_ThenThePropertyCannotBeSelected()
    {
        // Arrange
        var card = new Card(_context) { Content = new Card(_context) };
        var property = GetProperty(card, nameof(Card.Content));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, typeof(ValueMap));

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenAReferenceLeadsToNoSubject_ThenThePropertyCannotBeSelected()
    {
        // Arrange
        var card = new Card(_context);
        var property = GetProperty(card, nameof(Card.Content));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, typeof(ValueMap));

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenAReferenceIsSetToAnotherSubject_ThenTheSubjectItLeadsToNowDecides()
    {
        // Arrange
        var card = new Card(_context) { Content = new Card(_context) };
        var property = GetProperty(card, nameof(Card.Content));

        // Act
        var canSelectBefore = SubjectPathSelection.CanSelectProperty(property, typeof(ValueMap));
        card.Content = new ValueMap(_context);
        var canSelectAfter = SubjectPathSelection.CanSelectProperty(property, typeof(ValueMap));

        // Assert
        Assert.False(canSelectBefore);
        Assert.True(canSelectAfter);
    }

    [Fact]
    public void WhenPathLeadsToASubjectAndThePropertyIsNoReference_ThenThePropertyCannotBeSelected()
    {
        // Arrange - the collection holds a subject of the type that is asked for.
        var map = new ValueMap(_context) { Cases = [new ValueMapCase(_context)] };
        var card = new Card(_context) { Title = "Pump" };
        var collection = GetProperty(map, nameof(ValueMap.Cases));
        var value = GetProperty(card, nameof(Card.Title));

        // Act
        var canSelectCollection = SubjectPathSelection.CanSelectProperty(collection, typeof(ValueMapCase));
        var canSelectValue = SubjectPathSelection.CanSelectProperty(value, typeof(ValueMap));

        // Assert
        Assert.False(canSelectCollection);
        Assert.False(canSelectValue);
    }

    [Theory]
    [InlineData(typeof(Progress), nameof(Progress.Path), null)]
    [InlineData(typeof(Indicator), nameof(Indicator.Path), null)]
    [InlineData(typeof(Indicator), nameof(Indicator.Map), typeof(ValueMap))]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Path), null)]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Map), typeof(ValueMap))]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Path), null)]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Map), typeof(ValueMap))]
    public void WhenAValueWidgetIsRegistered_ThenItsPathPropertiesCarryTheAttributeForTheEditor(
        Type widgetType, string propertyName, Type? expectedSubjectType)
    {
        // Arrange
        var widget = (IInterceptorSubject)Activator.CreateInstance(widgetType, _context)!;

        // Act - the generated editor finds the attribute on the registered property, not on the type.
        var attribute = GetProperty(widget, propertyName).ReflectionAttributes.OfType<SubjectPathAttribute>().SingleOrDefault();

        // Assert
        Assert.NotNull(attribute);
        Assert.Equal(expectedSubjectType, attribute.SubjectType);
    }
}
