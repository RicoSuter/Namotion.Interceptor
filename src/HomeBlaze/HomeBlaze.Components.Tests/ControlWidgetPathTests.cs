using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ControlWidgetPathTests
{
    [Theory]
    [InlineData(typeof(Switch), nameof(Switch.Path), SubjectPathKinds.Boolean, false)]
    [InlineData(typeof(Switch), nameof(Switch.On), SubjectPathKinds.Operation, false)]
    [InlineData(typeof(Switch), nameof(Switch.Off), SubjectPathKinds.Operation, false)]
    public void WhenControlWidgetHasAPathArgument_ThenThePickerOffersOnlyTheTargetsItCanUse(
        Type widgetType, string propertyName, SubjectPathKinds expectedKind, bool expectedWritable)
    {
        // Arrange
        var property = widgetType.GetProperty(propertyName)!;

        // Act
        var attribute = property.GetCustomAttribute<SubjectPathAttribute>();

        // Assert
        Assert.NotNull(attribute);
        Assert.Null(attribute.SubjectType);
        Assert.Equal(expectedKind, attribute.Kind);
        Assert.Equal(expectedWritable, attribute.Writable);
    }
}
