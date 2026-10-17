using System.Numerics;
using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
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
        var canSelect = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute());

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
        var canSelectReference = SubjectPathSelection.CanSelectProperty(reference, new SubjectPathAttribute());
        var canSelectEmptyReference = SubjectPathSelection.CanSelectProperty(emptyReference, new SubjectPathAttribute());
        var canSelectCollection = SubjectPathSelection.CanSelectProperty(collection, new SubjectPathAttribute());

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
        var canSelect = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute(typeof(ValueMap)));

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
        var canSelect = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute(typeof(ValueMap)));

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
        var canSelect = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute(typeof(ValueMap)));

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
        var canSelectBefore = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute(typeof(ValueMap)));
        card.Content = new ValueMap(_context);
        var canSelectAfter = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute(typeof(ValueMap)));

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
        var canSelectCollection = SubjectPathSelection.CanSelectProperty(collection, new SubjectPathAttribute(typeof(ValueMapCase)));
        var canSelectValue = SubjectPathSelection.CanSelectProperty(value, new SubjectPathAttribute(typeof(ValueMap)));

        // Assert
        Assert.False(canSelectCollection);
        Assert.False(canSelectValue);
    }

    [Theory]
    [InlineData(typeof(Indicator), nameof(Indicator.Path))]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Path))]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Path))]
    public void WhenAValueWidgetIsRegistered_ThenItsPathLeadsToAPropertyOfAnyValueType(Type widgetType, string propertyName)
    {
        // Arrange
        var widget = (IInterceptorSubject)Activator.CreateInstance(widgetType, _context)!;

        // Act
        var attribute = GetProperty(widget, propertyName).ReflectionAttributes.OfType<SubjectPathAttribute>().SingleOrDefault();

        // Assert
        Assert.NotNull(attribute);
        Assert.Null(attribute.SubjectType);
        Assert.Equal(SubjectPathKind.Property, attribute.Kind);
        Assert.Null(attribute.ValueTypes);
        Assert.False(attribute.IsWritable);
    }

    [Theory]
    [InlineData(typeof(Indicator), nameof(Indicator.Map))]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Map))]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Map))]
    public void WhenAValueWidgetIsRegistered_ThenItsMapLeadsToAValueMap(Type widgetType, string propertyName)
    {
        // Arrange
        var widget = (IInterceptorSubject)Activator.CreateInstance(widgetType, _context)!;

        // Act
        var attribute = GetProperty(widget, propertyName).ReflectionAttributes.OfType<SubjectPathAttribute>().SingleOrDefault();

        // Assert
        Assert.NotNull(attribute);
        Assert.Equal(typeof(ValueMap), attribute.SubjectType);
    }

    [Fact]
    public void WhenAProgressIsRegistered_ThenItsPathLeadsToANumber()
    {
        // Act
        var attribute = typeof(Progress).GetProperty(nameof(Progress.Path))!.GetCustomAttribute<SubjectPathAttribute>()!;

        // Assert
        Assert.Equal(SubjectPathKind.Property, attribute.Kind);
        Assert.Equal([typeof(INumber<>)], attribute.ValueTypes);
        Assert.False(attribute.IsWritable);
    }

    [Fact]
    public void WhenAPathAttributeSetsNoFilter_ThenItAcceptsAPropertyOfAnyValueTypeWithOrWithoutASetter()
    {
        // Act
        var attribute = new SubjectPathAttribute();

        // Assert
        Assert.Equal(SubjectPathKind.Property, attribute.Kind);
        Assert.Null(attribute.ValueTypes);
        Assert.False(attribute.IsWritable);
    }

    [Theory]
    [InlineData(typeof(Indicator), nameof(Indicator.Path),
        "Count,DoubledSpeed,FallbackMode,Initial,IsOn,IsOpen,LastSeen,Limit,Mode,Name,Price,Reading,Speed,Temperature,Total")]
    [InlineData(typeof(ValueTile), nameof(ValueTile.Path),
        "Count,DoubledSpeed,FallbackMode,Initial,IsOn,IsOpen,LastSeen,Limit,Mode,Name,Price,Reading,Speed,Temperature,Total")]
    [InlineData(typeof(ValueRow), nameof(ValueRow.Path),
        "Count,DoubledSpeed,FallbackMode,Initial,IsOn,IsOpen,LastSeen,Limit,Mode,Name,Price,Reading,Speed,Temperature,Total")]
    [InlineData(typeof(Progress), nameof(Progress.Path), "Count,DoubledSpeed,Limit,Price,Reading,Speed,Temperature")]
    [InlineData(typeof(Gauge), nameof(Gauge.Path), "Count,DoubledSpeed,Limit,Price,Reading,Speed,Temperature")]
    [InlineData(typeof(Switch), nameof(Switch.Path), "IsOn,IsOpen")]
    [InlineData(typeof(Switch), nameof(Switch.On), "GetStatus,Stop")]
    [InlineData(typeof(Switch), nameof(Switch.Off), "GetStatus,Stop")]
    [InlineData(typeof(Slider), nameof(Slider.Path), "Count,Limit,Price,Speed,Temperature")]
    [InlineData(typeof(Select), nameof(Select.Path), "FallbackMode,Mode")]
    [InlineData(typeof(OperationButton), nameof(OperationButton.Path), "GetStatus,Stop")]
    [InlineData(typeof(Control), nameof(Control.Path),
        "Count,DoubledSpeed,FallbackMode,GetStatus,Initial,IsOn,IsOpen,LastSeen,Limit,Mode,Name,Price,Reading,Speed,Stop,Temperature,Total")]
    public void WhenAWidgetPathIsPicked_ThenItOffersTheTargetsTheWidgetCanUse(
        Type widgetType, string propertyName, string expectedPropertyNames)
    {
        // Arrange
        var attribute = widgetType.GetProperty(propertyName)!.GetCustomAttribute<SubjectPathAttribute>()!;
        var target = CreateTarget();

        // Act
        var selectable = target.TryGetRegisteredSubject()!.Properties
            .Where(property => SubjectPathSelection.CanSelectProperty(property, attribute))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);

        // Assert
        Assert.Equal(expectedPropertyNames, string.Join(",", selectable));
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.IsOn), "Boolean")]
    [InlineData(nameof(PathTargetSubject.IsOpen), "Boolean")]
    [InlineData(nameof(PathTargetSubject.Speed), "Number")]
    [InlineData(nameof(PathTargetSubject.Limit), "Number")]
    [InlineData(nameof(PathTargetSubject.Count), "Number")]
    [InlineData(nameof(PathTargetSubject.Temperature), "Number")]
    [InlineData(nameof(PathTargetSubject.Price), "Number")]
    [InlineData(nameof(PathTargetSubject.Mode), "Enum")]
    [InlineData(nameof(PathTargetSubject.FallbackMode), "Enum")]
    [InlineData(nameof(PathTargetSubject.Name), "String")]
    [InlineData(nameof(PathTargetSubject.Initial), "Char")]
    [InlineData(nameof(PathTargetSubject.LastSeen), "DateTimeOffset")]
    public void WhenAPropertyHasAValueType_ThenItMatchesThatValueTypeAndNoOther(string propertyName, string expectedValueType)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);

        // Act
        var matchingValueTypes = ValueTypeNames
            .Where(name => SubjectPathSelection.MatchesValueTypes(property.Type, GetValueTypes(name)))
            .ToArray();

        // Assert
        Assert.Equal([expectedValueType], matchingValueTypes);
    }

    [Theory]
    [InlineData(typeof(int?), typeof(INumber<>), true)]
    [InlineData(typeof(decimal), typeof(INumber<>), true)]
    [InlineData(typeof(Half), typeof(INumber<>), false)]
    [InlineData(typeof(Int128), typeof(INumber<>), false)]
    [InlineData(typeof(nint), typeof(INumber<>), false)]
    [InlineData(typeof(char), typeof(INumber<>), false)]
    [InlineData(typeof(char), typeof(IBinaryInteger<>), false)]
    [InlineData(typeof(char), typeof(IEquatable<>), true)]
    [InlineData(typeof(bool), typeof(INumber<>), false)]
    [InlineData(typeof(PathTargetMode), typeof(INumber<>), false)]
    [InlineData(typeof(string), typeof(INumber<>), false)]
    [InlineData(typeof(PathTargetMode?), typeof(Enum), true)]
    [InlineData(typeof(int), typeof(Enum), false)]
    [InlineData(typeof(bool?), typeof(bool), true)]
    [InlineData(typeof(int), typeof(bool), false)]
    [InlineData(typeof(List<int>), typeof(List<>), true)]
    [InlineData(typeof(List<int>), typeof(IEnumerable<>), true)]
    [InlineData(typeof(string), typeof(object), true)]
    public void WhenAValueTypeIsGiven_ThenATypeMatchesWhenItIsAssignableOrImplementsTheOpenGenericTypeAndNumbersAreTheReadableOnes(
        Type type, Type valueType, bool expected)
    {
        // Act
        var matches = SubjectPathSelection.MatchesValueTypes(type, [valueType]);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void WhenNoValueTypesAreGiven_ThenEveryTypeMatches()
    {
        // Act
        var matchesString = SubjectPathSelection.MatchesValueTypes(typeof(string), valueTypes: null);
        var matchesSubject = SubjectPathSelection.MatchesValueTypes(typeof(Card), valueTypes: null);

        // Assert
        Assert.True(matchesString);
        Assert.True(matchesSubject);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.IsOn), "Boolean", true)]
    [InlineData(nameof(PathTargetSubject.IsOpen), "Boolean", true)]
    [InlineData(nameof(PathTargetSubject.Speed), "Boolean", false)]
    [InlineData(nameof(PathTargetSubject.Limit), "Number", true)]
    [InlineData(nameof(PathTargetSubject.Mode), "Number", false)]
    [InlineData(nameof(PathTargetSubject.Initial), "Number", false)]
    [InlineData(nameof(PathTargetSubject.Total), "Number", false)]
    [InlineData(nameof(PathTargetSubject.FallbackMode), "Enum", true)]
    [InlineData(nameof(PathTargetSubject.Name), null, true)]
    [InlineData(nameof(PathTargetSubject.Stop), "Boolean", false)]
    [InlineData("GetStatus", null, false)]
    public void WhenValueTypesAreGiven_ThenOnlyAPropertyOfOneOfThemCanBeSelected(
        string propertyName, string? valueTypes, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(
            property, new SubjectPathAttribute { ValueTypes = GetValueTypes(valueTypes) });

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.IsOn), true)]
    [InlineData(nameof(PathTargetSubject.Temperature), true)]
    [InlineData(nameof(PathTargetSubject.FallbackMode), true)]
    [InlineData(nameof(PathTargetSubject.Name), false)]
    [InlineData(nameof(PathTargetSubject.Stop), false)]
    public void WhenValueTypesAreCombined_ThenAPropertyOfAnyOfThemCanBeSelected(string propertyName, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);
        var path = new SubjectPathAttribute { ValueTypes = [typeof(INumber<>), typeof(bool), typeof(Enum)] };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKind.Operation, true)]
    [InlineData("GetStatus", SubjectPathKind.Operation, true)]
    [InlineData(nameof(PathTargetSubject.IsOn), SubjectPathKind.Operation, false)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKind.Property, false)]
    [InlineData(nameof(PathTargetSubject.Speed), SubjectPathKind.Property | SubjectPathKind.Operation, true)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKind.Property | SubjectPathKind.Operation, true)]
    [InlineData(nameof(PathTargetSubject.Speed_Minimum), SubjectPathKind.Property, false)]
    [InlineData(nameof(PathTargetSubject.Speed_Minimum), SubjectPathKind.Attribute, true)]
    [InlineData(nameof(PathTargetSubject.Stop_IsEnabled), SubjectPathKind.Attribute, true)]
    [InlineData(nameof(PathTargetSubject.Speed), SubjectPathKind.Attribute, false)]
    [InlineData(nameof(PathTargetSubject.Stop), SubjectPathKind.Attribute, false)]
    public void WhenAKindIsGiven_ThenOnlyATargetOfThatKindCanBeSelected(
        string propertyName, SubjectPathKind kind, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute { Kind = kind });

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Fact]
    public void WhenAnOperationIsAllowed_ThenValueTypesDoNotRestrictIt()
    {
        // Arrange
        var property = GetProperty(CreateTarget(), nameof(PathTargetSubject.Stop));
        var path = new SubjectPathAttribute { Kind = SubjectPathKind.Operation, ValueTypes = [typeof(bool)] };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.True(canSelect);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.Speed_Minimum), "Number", true)]
    [InlineData(nameof(PathTargetSubject.Speed_Minimum), "Boolean", false)]
    [InlineData(nameof(PathTargetSubject.Stop_IsEnabled), "Boolean", true)]
    public void WhenValueTypesAreGivenForAnAttribute_ThenOnlyAnAttributeOfOneOfThemCanBeSelected(
        string propertyName, string valueTypes, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);
        var path = new SubjectPathAttribute { Kind = SubjectPathKind.Attribute, ValueTypes = GetValueTypes(valueTypes) };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Fact]
    public void WhenAnAttributeHoldsHomeBlazeMetadata_ThenItCannotBeSelected()
    {
        // Arrange
        var target = CreateTarget();
        var state = GetProperty(target, nameof(PathTargetSubject.Speed)).AddAttribute(
            KnownAttributes.State, typeof(StateMetadata), _ => new StateMetadata(), null);

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(state, new SubjectPathAttribute { Kind = SubjectPathKind.Attribute });

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenAttributesCanBeSelected_ThenThePickerListsThemBelowTheirProperty()
    {
        // Arrange
        var target = CreateTarget();
        var speed = GetProperty(target, nameof(PathTargetSubject.Speed));
        speed.AddAttribute(KnownAttributes.State, typeof(StateMetadata), _ => new StateMetadata(), null);
        var path = new SubjectPathAttribute { Kind = SubjectPathKind.Attribute };

        // Act
        var speedAttributes = SubjectPathSelection
            .GetListedAttributes(speed, property => SubjectPathSelection.CanSelectProperty(property, path))
            .Select(attribute => attribute.Name);
        var stopAttributes = SubjectPathSelection
            .GetListedAttributes(GetProperty(target, nameof(PathTargetSubject.Stop)), property => SubjectPathSelection.CanSelectProperty(property, path))
            .Select(attribute => attribute.Name);
        var propertyAttributes = SubjectPathSelection
            .GetListedAttributes(speed, property => SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute()));

        // Assert
        Assert.Equal([nameof(PathTargetSubject.Speed_Minimum)], speedAttributes);
        Assert.Equal([nameof(PathTargetSubject.Stop_IsEnabled)], stopAttributes);
        Assert.Empty(propertyAttributes);
    }

    [Fact]
    public void WhenOnlyANestedAttributeCanBeSelected_ThenThePickerListsTheAttributeOnTheWayToIt()
    {
        // Arrange
        var target = CreateTarget();
        var speed = GetProperty(target, nameof(PathTargetSubject.Speed));
        var unit = GetProperty(target, nameof(PathTargetSubject.Speed_Minimum)).AddAttribute("Unit", typeof(string), _ => "rpm", null);
        var path = new SubjectPathAttribute { Kind = SubjectPathKind.Attribute, ValueTypes = [typeof(string)] };
        Func<RegisteredSubjectProperty, bool> canSelectProperty = property => SubjectPathSelection.CanSelectProperty(property, path);

        // Act
        var speedAttributes = SubjectPathSelection.GetListedAttributes(speed, canSelectProperty).ToArray();
        var minimumAttributes = SubjectPathSelection.GetListedAttributes(speedAttributes.Single(), canSelectProperty).ToArray();

        // Assert
        Assert.Equal(nameof(PathTargetSubject.Speed_Minimum), speedAttributes.Single().Name);
        Assert.False(canSelectProperty(speedAttributes.Single()));
        Assert.Same(unit, minimumAttributes.Single());
        Assert.True(canSelectProperty(unit));
    }

    [Fact]
    public void WhenAnAttributeBelongsToAProperty_ThenItIsAnAttributeOfItDirectlyOrNested()
    {
        // Arrange
        var target = CreateTarget();
        var speed = GetProperty(target, nameof(PathTargetSubject.Speed));
        var minimum = GetProperty(target, nameof(PathTargetSubject.Speed_Minimum));
        var unit = minimum.AddAttribute("Unit", typeof(string), _ => "rpm", null);

        // Act & Assert
        Assert.True(SubjectPathSelection.IsAttributeOf(minimum, speed));
        Assert.True(SubjectPathSelection.IsAttributeOf(unit, speed));
        Assert.True(SubjectPathSelection.IsAttributeOf(unit, minimum));
        Assert.False(SubjectPathSelection.IsAttributeOf(minimum, GetProperty(target, nameof(PathTargetSubject.Stop))));
        Assert.False(SubjectPathSelection.IsAttributeOf(speed, speed));
        Assert.False(SubjectPathSelection.IsAttributeOf(null, speed));
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.Speed), "Number", true)]
    [InlineData(nameof(PathTargetSubject.IsOpen), "Boolean", true)]
    [InlineData(nameof(PathTargetSubject.Reading), "Number", false)]
    [InlineData(nameof(PathTargetSubject.DoubledSpeed), "Number", false)]
    [InlineData(nameof(PathTargetSubject.IsOn), "Number", false)]
    public void WhenWritableIsSet_ThenOnlyAPropertyOfTheValueTypesWithAPublicSetterCanBeSelected(
        string propertyName, string valueTypes, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);
        var path = new SubjectPathAttribute { ValueTypes = GetValueTypes(valueTypes), IsWritable = true };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Theory]
    [InlineData(nameof(PathTargetSubject.Speed_Minimum), true)]
    [InlineData(nameof(PathTargetSubject.Stop_IsEnabled), false)]
    public void WhenWritableIsSetForAnAttribute_ThenOnlyAnAttributeWithAPublicSetterCanBeSelected(string propertyName, bool expected)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), propertyName);
        var path = new SubjectPathAttribute { Kind = SubjectPathKind.Attribute, IsWritable = true };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.Equal(expected, canSelect);
    }

    [Theory]
    [InlineData(SubjectPathKind.Operation)]
    [InlineData(SubjectPathKind.Property | SubjectPathKind.Operation)]
    public void WhenWritableIsSet_ThenNoOperationCanBeSelected(SubjectPathKind kind)
    {
        // Arrange
        var property = GetProperty(CreateTarget(), nameof(PathTargetSubject.Stop));

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, new SubjectPathAttribute { Kind = kind, IsWritable = true });

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenWritableIsNotSet_ThenAPropertyWithoutAPublicSetterCanBeSelected()
    {
        // Arrange
        var target = CreateTarget();
        var path = new SubjectPathAttribute { ValueTypes = [typeof(INumber<>)] };

        // Act
        var canSelectInternalSetter = SubjectPathSelection.CanSelectProperty(GetProperty(target, nameof(PathTargetSubject.Reading)), path);
        var canSelectDerived = SubjectPathSelection.CanSelectProperty(GetProperty(target, nameof(PathTargetSubject.DoubledSpeed)), path);

        // Assert
        Assert.True(canSelectInternalSetter);
        Assert.True(canSelectDerived);
    }

    [Fact]
    public void WhenNoKindIsGiven_ThenNeitherAnOperationNorAnAttributeCanBeSelected()
    {
        // Arrange
        var target = CreateTarget();

        // Act
        var canSelectOperation = SubjectPathSelection.CanSelectProperty(GetProperty(target, nameof(PathTargetSubject.Stop)), new SubjectPathAttribute());
        var canSelectAttribute = SubjectPathSelection.CanSelectProperty(GetProperty(target, nameof(PathTargetSubject.Speed_Minimum)), new SubjectPathAttribute());

        // Assert
        Assert.False(canSelectOperation);
        Assert.False(canSelectAttribute);
    }

    [Fact]
    public void WhenAPropertyHoldsSubjects_ThenNoKindMakesItSelectable()
    {
        // Arrange
        var card = new Card(_context) { Content = new ValueMap(_context) };
        var property = GetProperty(card, nameof(Card.Content));
        var path = new SubjectPathAttribute { Kind = SubjectPathKind.Property | SubjectPathKind.Attribute | SubjectPathKind.Operation };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.False(canSelect);
    }

    [Fact]
    public void WhenPathLeadsToASubject_ThenKindValueTypesAndWritableAreIgnored()
    {
        // Arrange
        var card = new Card(_context) { Content = new ValueMap(_context) };
        var property = GetProperty(card, nameof(Card.Content));
        var path = new SubjectPathAttribute(typeof(ValueMap))
        {
            Kind = SubjectPathKind.Operation,
            ValueTypes = [typeof(bool)],
            IsWritable = true
        };

        // Act
        var canSelect = SubjectPathSelection.CanSelectProperty(property, path);

        // Assert
        Assert.True(canSelect);
    }

    [Fact]
    public void WhenAWidgetPathLeadsToASubject_ThenItSetsNoKindValueTypesOrWritable()
    {
        // Arrange
        var subjectPaths = typeof(Card).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<SubjectPathAttribute>()))
            .Where(pair => pair.Attribute?.SubjectType is not null)
            .ToList();

        // Act
        var combined = subjectPaths
            .Where(pair => pair.Attribute!.Kind != SubjectPathKind.Property || pair.Attribute.ValueTypes is not null || pair.Attribute.IsWritable)
            .Select(pair => $"{pair.Property.DeclaringType!.Name}.{pair.Property.Name}")
            .ToList();

        // Assert
        Assert.NotEmpty(subjectPaths);
        Assert.Empty(combined);
    }

    private static readonly string[] ValueTypeNames = ["Boolean", "Number", "Enum", "String", "Char", "DateTimeOffset"];

    private static Type[]? GetValueTypes(string? name) => name switch
    {
        null => null,
        "Boolean" => [typeof(bool)],
        "Number" => [typeof(INumber<>)],
        "Enum" => [typeof(Enum)],
        "String" => [typeof(string)],
        "Char" => [typeof(char)],
        "DateTimeOffset" => [typeof(DateTimeOffset)],
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };
}
