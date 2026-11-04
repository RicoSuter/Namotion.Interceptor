using System.Numerics;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ControlWidgetPathTests
{
    public static TheoryData<Type, string, SubjectPathKind, Type[]?, bool> PathArguments => new()
    {
        { typeof(Switch), nameof(Switch.Path), SubjectPathKind.Property, [typeof(bool)], false },
        { typeof(Switch), nameof(Switch.On), SubjectPathKind.Operation, null, false },
        { typeof(Switch), nameof(Switch.Off), SubjectPathKind.Operation, null, false },
        { typeof(Slider), nameof(Slider.Path), SubjectPathKind.Property, [typeof(INumber<>)], true },
        { typeof(Select), nameof(Select.Path), SubjectPathKind.Property, [typeof(Enum)], true },
        { typeof(OperationButton), nameof(OperationButton.Path), SubjectPathKind.Operation, null, false },
        { typeof(Control), nameof(Control.Path), SubjectPathKind.Property | SubjectPathKind.Operation, null, false },
    };

    [Theory]
    [MemberData(nameof(PathArguments))]
    public void WhenControlWidgetHasAPathArgument_ThenThePickerOffersOnlyTheTargetsItCanUse(
        Type widgetType, string propertyName, SubjectPathKind expectedKind, Type[]? expectedValueTypes, bool expectedWritable)
    {
        // Arrange
        var property = widgetType.GetProperty(propertyName)!;

        // Act
        var attribute = property.GetCustomAttribute<SubjectPathAttribute>();

        // Assert
        Assert.NotNull(attribute);
        Assert.Null(attribute.SubjectType);
        Assert.Equal(expectedKind, attribute.Kind);
        Assert.Equal(expectedValueTypes, attribute.ValueTypes);
        Assert.Equal(expectedWritable, attribute.IsWritable);
    }
}
