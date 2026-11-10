using System.Reflection;

namespace HomeBlaze.Services.Tests;

public class MemberNullabilityTests
{
    [Theory]
    [InlineData(typeof(string), true)]
    [InlineData(typeof(int?), true)]
    [InlineData(typeof(int), false)]
    public void WhenTypeIsChecked_ThenReferenceTypesAndNullableValueTypesAcceptNull(Type type, bool expected)
    {
        // Act
        var acceptsNull = MemberNullability.AcceptsNull(type);

        // Assert
        Assert.Equal(expected, acceptsNull);
    }

    [Theory]
    [InlineData(nameof(Members.NonNullableText), false)]
    [InlineData(nameof(Members.NullableText), true)]
    [InlineData(nameof(Members.UnannotatedText), true)]
    [InlineData(nameof(Members.NullableNumber), true)]
    [InlineData(nameof(Members.Number), false)]
    public void WhenPropertyIsChecked_ThenOnlyNonNullableAnnotationsAndValueTypesRejectNull(string propertyName, bool expected)
    {
        // Arrange
        var property = typeof(Members).GetProperty(propertyName)!;

        // Act
        var acceptsNull = MemberNullability.AcceptsNull(property);

        // Assert
        Assert.Equal(expected, acceptsNull);
    }

    [Fact]
    public void WhenPropertyIsCheckedTwice_ThenTheResultIsTheSame()
    {
        // Arrange
        var property = typeof(Members).GetProperty(nameof(Members.NonNullableText))!;

        // Act
        var first = MemberNullability.AcceptsNull(property);
        var second = MemberNullability.AcceptsNull(property);

        // Assert
        Assert.False(first);
        Assert.False(second);
    }

    [Theory]
    [InlineData("nonNullableText", NullabilityState.NotNull)]
    [InlineData("nullableText", NullabilityState.Nullable)]
    [InlineData("unannotatedText", NullabilityState.Unknown)]
    [InlineData("number", NullabilityState.NotNull)]
    public void WhenParameterIsRead_ThenItsAnnotatedWriteStateIsReturned(string parameterName, NullabilityState expected)
    {
        // Arrange
        var parameter = GetParameter(parameterName);
        var context = MemberNullability.TryCreateContext();

        // Act
        var state = MemberNullability.GetWriteState(context, parameter);

        // Assert
        Assert.Equal(expected, state);
    }

    [Fact]
    public void WhenParameterIsReadWithoutContext_ThenItsWriteStateIsUnknown()
    {
        // Arrange
        var parameter = GetParameter("nullableText");

        // Act
        var state = MemberNullability.GetWriteState(null, parameter);

        // Assert
        Assert.Equal(NullabilityState.Unknown, state);
    }

    private static ParameterInfo GetParameter(string name) =>
        Array.Find(typeof(Members).GetMethod(nameof(Members.Method))!.GetParameters(), parameter => parameter.Name == name)!;

    private sealed class Members
    {
        public string NonNullableText { get; set; } = "";

        public string? NullableText { get; set; }

        public int? NullableNumber { get; set; }

        public int Number { get; set; }

#nullable disable
        public string UnannotatedText { get; set; }

        public void Method(
#nullable enable
            string nonNullableText,
            string? nullableText,
            int number,
#nullable disable
            string unannotatedText)
#nullable enable
        {
        }
    }
}
