using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Inputs;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
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

    private static PathTargetSubject CreateTarget()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        return new PathTargetSubject(context);
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
        // Arrange
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
    [InlineData(typeof(Progress), nameof(Progress.Path), null, SubjectPathKinds.Number)]
    [InlineData(typeof(Indicator), nameof(Indicator.Path), null, SubjectPathKinds.Value)]
    [InlineData(typeof(Indicator), nameof(Indicator.Map), typeof(ValueMap), SubjectPathKinds.Value)]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Path), null, SubjectPathKinds.Value)]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Map), typeof(ValueMap), SubjectPathKinds.Value)]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Path), null, SubjectPathKinds.Value)]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Map), typeof(ValueMap), SubjectPathKinds.Value)]
    public void WhenAValueWidgetIsRegistered_ThenItsPathPropertiesCarryTheAttributeForTheEditor(
        Type widgetType, string propertyName, Type? expectedSubjectType, SubjectPathKinds expectedKind)
    {
        // Arrange
        var widget = (IInterceptorSubject)Activator.CreateInstance(widgetType, _context)!;

        // Act
        var attribute = GetProperty(widget, propertyName).ReflectionAttributes.OfType<SubjectPathAttribute>().SingleOrDefault();

        // Assert
        Assert.NotNull(attribute);
        Assert.Equal(expectedSubjectType, attribute.SubjectType);
        Assert.Equal(expectedKind, attribute.Kind);
        Assert.False(attribute.Writable);
    }

    [Fact]
    public void WhenAPathAttributeSetsNoFilter_ThenItAcceptsEveryValueWithOrWithoutASetter()
    {
        // Act
        var attribute = new SubjectPathAttribute();

        // Assert
        Assert.Equal(SubjectPathKinds.Value, attribute.Kind);
        Assert.False(attribute.Writable);
        Assert.Equal(
            SubjectPathKinds.Boolean | SubjectPathKinds.Number | SubjectPathKinds.Enum | SubjectPathKinds.OtherValue,
            SubjectPathKinds.Value);
        Assert.False(SubjectPathKinds.Value.HasFlag(SubjectPathKinds.Operation));
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.IsOn), SubjectPathKinds.Boolean)]
    [InlineData(nameof(PathTargetSubject.IsOpen), SubjectPathKinds.Boolean)]
    [InlineData(nameof(PathTargetSubject.Speed), SubjectPathKinds.Number)]
    [InlineData(nameof(PathTargetSubject.Limit), SubjectPathKinds.Number)]
    [InlineData(nameof(PathTargetSubject.Count), SubjectPathKinds.Number)]
    [InlineData(nameof(PathTargetSubject.Temperature), SubjectPathKinds.Number)]
    [InlineData(nameof(PathTargetSubject.Price), SubjectPathKinds.Number)]
    [InlineData(nameof(PathTargetSubject.Mode), SubjectPathKinds.Enum)]
    [InlineData(nameof(PathTargetSubject.FallbackMode), SubjectPathKinds.Enum)]
    [InlineData(nameof(PathTargetSubject.Name), SubjectPathKinds.OtherValue)]
    [InlineData(nameof(PathTargetSubject.LastSeen), SubjectPathKinds.OtherValue)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKinds.Operation)]
    [InlineData("GetStatus", SubjectPathKinds.Operation)]
    public void WhenAPropertyIsOfAKind_ThenItMatchesThatKindAndNoOther(string propertyName, SubjectPathKinds expectedKind)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);
        SubjectPathKinds[] singleKinds =
        [
            SubjectPathKinds.Boolean,
            SubjectPathKinds.Number,
            SubjectPathKinds.Enum,
            SubjectPathKinds.OtherValue,
            SubjectPathKinds.Operation
        ];

        // Act
        var matchingKinds = singleKinds.Where(kind => SubjectPathSelection.MatchesKind(property, kind)).ToArray();

        // Assert
        Assert.Equal(new[] { expectedKind }, matchingKinds);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.IsOn), SubjectPathKinds.Boolean, true)]
    [InlineData(nameof(PathTargetSubject.IsOpen), SubjectPathKinds.Boolean, true)]
    [InlineData(nameof(PathTargetSubject.Speed), SubjectPathKinds.Boolean, false)]
    [InlineData(nameof(PathTargetSubject.Limit), SubjectPathKinds.Number, true)]
    [InlineData(nameof(PathTargetSubject.Mode), SubjectPathKinds.Number, false)]
    [InlineData(nameof(PathTargetSubject.FallbackMode), SubjectPathKinds.Enum, true)]
    [InlineData(nameof(PathTargetSubject.Name), SubjectPathKinds.Value, true)]
    [InlineData(nameof(PathTargetSubject.IsOn), SubjectPathKinds.Operation, false)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKinds.Operation, true)]
    [InlineData("GetStatus", SubjectPathKinds.Operation, true)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKinds.Value, false)]
    [InlineData("GetStatus", SubjectPathKinds.Value, false)]
    public void WhenAKindIsGiven_ThenOnlyAPropertyOfThatKindCanBeSelected(
        string propertyName, SubjectPathKinds kind, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, subjectType: null, kind, writable: false);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.IsOn), true)]
    [InlineData(nameof(PathTargetSubject.Temperature), true)]
    [InlineData(nameof(PathTargetSubject.FallbackMode), true)]
    [InlineData(nameof(PathTargetSubject.Name), false)]
    [InlineData(nameof(PathTargetSubject.Stop), false)]
    public void WhenKindsAreCombined_ThenAPropertyOfAnyOfThemCanBeSelected(string propertyName, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);
        const SubjectPathKinds kind = SubjectPathKinds.Boolean | SubjectPathKinds.Number | SubjectPathKinds.Enum;

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, subjectType: null, kind, writable: false);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Fact]
    public void WhenValueAndOperationAreCombined_ThenValuesAndOperationsCanBeSelected()
    {
        // Arrange
        var target = CreateTarget();
        const SubjectPathKinds kind = SubjectPathKinds.Value | SubjectPathKinds.Operation;

        // Act
        var canSelectValue = SubjectPathSelection.CanSelectProperty(
            GetProperty(target, nameof(PathTargetSubject.Speed)), subjectType: null, kind, writable: false);
        var canSelectOperation = SubjectPathSelection.CanSelectProperty(
            GetProperty(target, nameof(PathTargetSubject.Stop)), subjectType: null, kind, writable: false);

        // Assert
        Assert.True(canSelectValue);
        Assert.True(canSelectOperation);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.Speed), SubjectPathKinds.Number, true)]
    [InlineData(nameof(PathTargetSubject.IsOpen), SubjectPathKinds.Boolean, true)]
    [InlineData(nameof(PathTargetSubject.Reading), SubjectPathKinds.Number, false)]
    [InlineData(nameof(PathTargetSubject.DoubledSpeed), SubjectPathKinds.Number, false)]
    [InlineData(nameof(PathTargetSubject.IsOn), SubjectPathKinds.Number, false)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKinds.Operation, false)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKinds.Value | SubjectPathKinds.Operation, false)]
    public void WhenWritableIsSet_ThenOnlyAPropertyOfTheKindWithAPublicSetterCanBeSelected(
        string propertyName, SubjectPathKinds kind, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, subjectType: null, kind, writable: true);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Fact]
    public void WhenWritableIsNotSet_ThenAPropertyWithoutAPublicSetterCanBeSelected()
    {
        // Arrange
        var target = CreateTarget();

        // Act
        var canSelectInternalSetter = SubjectPathSelection.CanSelectProperty(
            GetProperty(target, nameof(PathTargetSubject.Reading)), subjectType: null, SubjectPathKinds.Number, writable: false);
        var canSelectDerived = SubjectPathSelection.CanSelectProperty(
            GetProperty(target, nameof(PathTargetSubject.DoubledSpeed)), subjectType: null, SubjectPathKinds.Number, writable: false);

        // Assert
        Assert.True(canSelectInternalSetter);
        Assert.True(canSelectDerived);
    }

    [Fact]
    public void WhenNoKindIsGiven_ThenAnOperationCannotBeSelected()
    {
        // Arrange
        var property = GetProperty(CreateTarget(), nameof(PathTargetSubject.Stop));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, subjectType: null);

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenAPropertyHoldsSubjects_ThenNoKindMakesItSelectable()
    {
        // Arrange
        var card = new Card(_context) { Content = new ValueMap(_context) };
        var property = GetProperty(card, nameof(Card.Content));
        const SubjectPathKinds kind = SubjectPathKinds.Value | SubjectPathKinds.Operation;

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, subjectType: null, kind, writable: false);

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenPathLeadsToASubject_ThenKindAndWritableAreIgnored()
    {
        // Arrange
        var card = new Card(_context) { Content = new ValueMap(_context) };
        var property = GetProperty(card, nameof(Card.Content));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(
            property, typeof(ValueMap), SubjectPathKinds.Operation, writable: true);

        // Assert
        Assert.True(canSelect);
    }

    [Fact]
    public void WhenAWidgetPathLeadsToASubject_ThenItSetsNeitherKindNorWritable()
    {
        // Arrange
        var subjectPaths = typeof(Card).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<SubjectPathAttribute>()))
            .Where(pair => pair.Attribute?.SubjectType is not null)
            .ToList();

        // Act
        var combined = subjectPaths
            .Where(pair => pair.Attribute!.Kind != SubjectPathKinds.Value || pair.Attribute.Writable)
            .Select(pair => $"{pair.Property.DeclaringType!.Name}.{pair.Property.Name}")
            .ToList();

        // Assert
        Assert.NotEmpty(subjectPaths);
        Assert.Empty(combined);
    }
}
