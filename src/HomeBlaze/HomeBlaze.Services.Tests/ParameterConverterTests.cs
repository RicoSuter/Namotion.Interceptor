using System.Text.Json.Serialization;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Services.Tests;

public class ParameterConverterTests
{
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(TimeSpan))]
    public void IsSupported_ReturnsTrueForPrimitiveTypes(Type type)
    {
        // Act
        var result = ParameterConverter.IsSupported(type);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(typeof(int?))]
    [InlineData(typeof(bool?))]
    [InlineData(typeof(DateTime?))]
    [InlineData(typeof(Guid?))]
    public void IsSupported_ReturnsTrueForNullablePrimitives(Type type)
    {
        // Act
        var result = ParameterConverter.IsSupported(type);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSupported_ReturnsTrueForEnums()
    {
        // Act
        var result = ParameterConverter.IsSupported(typeof(DayOfWeek));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSupported_ReturnsTrueForNullableEnums()
    {
        // Act
        var result = ParameterConverter.IsSupported(typeof(DayOfWeek?));

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(Dictionary<string, int>))]
    public void IsSupported_ReturnsFalseForComplexTypes(Type type)
    {
        // Act
        var result = ParameterConverter.IsSupported(type);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryConvert_String_ReturnsInput()
    {
        // Act
        var success = ParameterConverter.TryConvert("hello", typeof(string), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal("hello", value);
    }

    [Fact]
    public void TryConvert_EmptyStringToNullableString_ReturnsNull()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(string), out var value);

        // Assert
        Assert.True(success);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("-456", -456)]
    [InlineData("0", 0)]
    public void TryConvert_Int_ParsesCorrectly(string input, int expected)
    {
        // Act
        var success = ParameterConverter.TryConvert(input, typeof(int), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryConvert_InvalidInt_ReturnsFalse()
    {
        // Act
        var success = ParameterConverter.TryConvert("abc", typeof(int), out var value);

        // Assert
        Assert.False(success);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public void TryConvert_Bool_ParsesCorrectly(string input, bool expected)
    {
        // Act
        var success = ParameterConverter.TryConvert(input, typeof(bool), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryConvert_Guid_ParsesCorrectly()
    {
        // Arrange
        var expected = Guid.NewGuid();

        // Act
        var success = ParameterConverter.TryConvert(expected.ToString(), typeof(Guid), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryConvert_TimeSpan_ParsesCorrectly()
    {
        // Act
        var success = ParameterConverter.TryConvert("01:30:00", typeof(TimeSpan), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(TimeSpan.FromMinutes(90), value);
    }

    [Fact]
    public void TryConvert_Enum_ParsesCorrectly()
    {
        // Act
        var success = ParameterConverter.TryConvert("Monday", typeof(DayOfWeek), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(DayOfWeek.Monday, value);
    }

    [Fact]
    public void TryConvert_Enum_IgnoresCase()
    {
        // Act
        var success = ParameterConverter.TryConvert("monday", typeof(DayOfWeek), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(DayOfWeek.Monday, value);
    }

    [Fact]
    public void TryConvert_NullableInt_ParsesCorrectly()
    {
        // Act
        var success = ParameterConverter.TryConvert("42", typeof(int?), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryConvert_EmptyToNullableInt_ReturnsNull()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(int?), out var value);

        // Assert
        Assert.True(success);
        Assert.Null(value);
    }

    [Fact]
    public void TryConvert_EmptyToRequiredInt_ReturnsFalse()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(int), out var value);

        // Assert
        Assert.False(success);
    }

    [Theory]
    [InlineData("20", "0.2")]
    [InlineData("100", "1")]
    [InlineData("12.5", "0.125")]
    [InlineData("-100", "-1")]
    [InlineData("0", "0")]
    public void WhenPercentDecimalIsEntered_ThenTheFractionIsReturned(string input, string expected)
    {
        // Act
        var success = ParameterConverter.TryConvert(input, typeof(decimal), StateUnit.Percent, out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void WhenPercentDoubleIsEntered_ThenTheFractionIsReturned()
    {
        // Act
        var success = ParameterConverter.TryConvert("33.3", typeof(double), StateUnit.Percent, out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(0.333, value);
    }

    [Fact]
    public void WhenPercentNullableDecimalIsEntered_ThenTheFractionIsReturned()
    {
        // Act
        var success = ParameterConverter.TryConvert("50", typeof(decimal?), StateUnit.Percent, out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(0.5m, value);
    }

    [Fact]
    public void WhenPercentNullableDecimalIsEmpty_ThenNullIsReturned()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(decimal?), StateUnit.Percent, out var value);

        // Assert
        Assert.True(success);
        Assert.Null(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(StateUnit.Default)]
    [InlineData(StateUnit.DegreeCelsius)]
    public void WhenUnitIsNotPercent_ThenTheValueIsUnchanged(StateUnit? unit)
    {
        // Act
        var success = ParameterConverter.TryConvert("20", typeof(decimal), unit, out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(20m, value);
    }

    [Fact]
    public void WhenPercentInputIsInvalid_ThenConversionFails()
    {
        // Act
        var success = ParameterConverter.TryConvert("abc", typeof(decimal), StateUnit.Percent, out _);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public void WhenRecordHasOnlySimpleMembers_ThenIsSupportedObject()
    {
        // Act & Assert
        Assert.True(ParameterConverter.IsSupportedObject(typeof(TestMessage)));
        Assert.False(ParameterConverter.IsSupportedObject(typeof(string)));
        Assert.False(ParameterConverter.IsSupportedObject(typeof(TestEnvelope)));
    }

    [Fact]
    public void WhenTypeIsConcreteObject_ThenIsJsonObject()
    {
        // Act & Assert
        Assert.True(ParameterConverter.IsJsonObject(typeof(TestEnvelope)));
        Assert.True(ParameterConverter.IsJsonObject(typeof(TestMessage)));
        Assert.False(ParameterConverter.IsJsonObject(typeof(IDisposable)));
        Assert.False(ParameterConverter.IsJsonObject(typeof(int)));
        Assert.False(ParameterConverter.IsJsonObject(typeof(object)));
    }

    [Fact]
    public void WhenGettingObjectMembers_ThenRequiredAndNullableAreDescribed()
    {
        // Act
        var members = ParameterConverter.GetObjectMembers(typeof(TestMessage));

        // Assert
        Assert.Equal(["Text", "Title", "Level", "Count"], members.Select(member => member.Name));
        Assert.True(members[0].IsRequired);
        Assert.False(members[0].IsNullable);
        Assert.True(members[1].IsNullable);
        Assert.False(members[2].IsRequired);
        Assert.True(members[3].IsNullable);
    }

    [Fact]
    public void WhenCreatingObjectFromInputs_ThenMembersAreConvertedAndDefaultsKept()
    {
        // Arrange
        var inputs = new Dictionary<string, string?> { ["Text"] = "Hello", ["Count"] = "3" };

        // Act
        var success = ParameterConverter.TryCreateObject(typeof(TestMessage), inputs, out var value, out var error);

        // Assert
        Assert.True(success, error);
        var message = Assert.IsType<TestMessage>(value);
        Assert.Equal("Hello", message.Text);
        Assert.Null(message.Title);
        Assert.Equal(TestLevel.Normal, message.Level);
        Assert.Equal(3, message.Count);
    }

    [Fact]
    public void WhenCreatingObjectWithEnumInput_ThenEnumIsSet()
    {
        // Arrange
        var inputs = new Dictionary<string, string?> { ["Text"] = "Hello", ["Level"] = "High" };

        // Act
        var success = ParameterConverter.TryCreateObject(typeof(TestMessage), inputs, out var value, out _);

        // Assert
        Assert.True(success);
        Assert.Equal(TestLevel.High, Assert.IsType<TestMessage>(value).Level);
    }

    [Fact]
    public void WhenRequiredMemberHasNoInput_ThenCreatingFails()
    {
        // Act
        var success = ParameterConverter.TryCreateObject(typeof(TestMessage), new Dictionary<string, string?>(), out var value, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(value);
        Assert.Equal("Text is required", error);
    }

    [Fact]
    public void WhenMemberInputIsInvalid_ThenCreatingFails()
    {
        // Arrange
        var inputs = new Dictionary<string, string?> { ["Text"] = "Hello", ["Count"] = "many" };

        // Act
        var success = ParameterConverter.TryCreateObject(typeof(TestMessage), inputs, out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("Invalid value for Count", error);
    }

    [Fact]
    public void WhenParsingValidJson_ThenObjectIsCreatedWithAnyNameCase()
    {
        // Act
        var success = ParameterConverter.TryParseJson("""{"text":"Hi","inner":{"value":2}}""", typeof(TestEnvelope), isNullable: false, out var value, out var error);

        // Assert
        Assert.True(success, error);
        var envelope = Assert.IsType<TestEnvelope>(value);
        Assert.Equal("Hi", envelope.Text);
        Assert.Equal(2, envelope.Inner!.Value);
    }

    [Fact]
    public void WhenParsingInvalidJson_ThenErrorIsReturned()
    {
        // Act
        var success = ParameterConverter.TryParseJson("{not json", typeof(TestEnvelope), isNullable: false, out var value, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(value);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WhenJsonIsEmpty_ThenOnlyANullableParameterAcceptsIt(bool isNullable, bool expectedSuccess)
    {
        // Act
        var success = ParameterConverter.TryParseJson("  ", typeof(TestEnvelope), isNullable, out var value, out _);

        // Assert
        Assert.Equal(expectedSuccess, success);
        Assert.Null(value);
    }

    [Fact]
    public void WhenMemberIsAlwaysJsonIgnored_ThenItIsExcluded()
    {
        // Act
        var members = ParameterConverter.GetObjectMembers(typeof(TestIgnoreMessage));

        // Assert
        Assert.DoesNotContain(members, member => member.Name == nameof(TestIgnoreMessage.Secret));
        Assert.Contains(members, member => member.Name == nameof(TestIgnoreMessage.Note));
    }

    [Fact]
    public void WhenDerivedMemberHidesBaseMemberWithNew_ThenOnlyTheDerivedMemberIsListed()
    {
        // Act
        var members = ParameterConverter.GetObjectMembers(typeof(TestDerivedWithHiddenMember));

        // Assert
        var member = Assert.Single(members, candidate => candidate.Name == "Value");
        Assert.Equal(typeof(string), member.Type);
    }

    [Fact]
    public void WhenDerivedMemberHidingBaseIsJsonIgnored_ThenTheMemberIsExcludedEntirely()
    {
        // Act
        var members = ParameterConverter.GetObjectMembers(typeof(TestDerivedHidingWithIgnoredMember));

        // Assert
        Assert.DoesNotContain(members, member => member.Name == "Code");
    }

    [Fact]
    public void WhenMemberHasJsonPropertyName_ThenInputBindsByClrName()
    {
        // Arrange
        var inputs = new Dictionary<string, string?> { ["DisplayName"] = "Hello" };

        // Act
        var success = ParameterConverter.TryCreateObject(typeof(TestRenamedMessage), inputs, out var value, out var error);

        // Assert
        Assert.True(success, error);
        Assert.Equal("Hello", Assert.IsType<TestRenamedMessage>(value).DisplayName);
    }

    [Fact]
    public void WhenCreatingObjectWithNaNInput_ThenDoubleMemberIsSetToNaN()
    {
        // Arrange
        var inputs = new Dictionary<string, string?> { ["Value"] = "NaN" };

        // Act
        var success = ParameterConverter.TryCreateObject(typeof(TestDoubleMessage), inputs, out var value, out var error);

        // Assert
        Assert.True(success, error);
        Assert.True(double.IsNaN(Assert.IsType<TestDoubleMessage>(value).Value));
    }

    [Fact]
    public void WhenSetterThrowsArgumentException_ThenParsingFailsWithExceptionMessage()
    {
        // Act
        var success = ParameterConverter.TryParseJson("""{"text":""}""", typeof(TestThrowingMessage), isNullable: false, out var value, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(value);
        Assert.Equal("Text must not be empty", error);
    }

    [Fact]
    public void WhenJsonIsNullLiteral_ThenOnlyANullableParameterAcceptsIt()
    {
        // Act
        var success = ParameterConverter.TryParseJson("null", typeof(TestEnvelope), isNullable: false, out var value, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(value);
        Assert.Equal("This field is required", error);
    }

    [Fact]
    public void WhenJsonIsNullLiteralAndParameterIsNullable_ThenParsingSucceedsWithNull()
    {
        // Act
        var success = ParameterConverter.TryParseJson("null", typeof(TestEnvelope), isNullable: true, out var value, out _);

        // Assert
        Assert.True(success);
        Assert.Null(value);
    }

    [Fact]
    public void WhenTypeIsCollectionOrArray_ThenItIsJsonObjectButNotSupportedObject()
    {
        // Act & Assert
        Assert.False(ParameterConverter.IsSupportedObject(typeof(List<int>)));
        Assert.True(ParameterConverter.IsJsonObject(typeof(List<int>)));
        Assert.False(ParameterConverter.IsSupportedObject(typeof(int[])));
        Assert.True(ParameterConverter.IsJsonObject(typeof(int[])));
    }

    [Fact]
    public void WhenTypeIsDelegate_ThenItIsNeitherSupportedObjectNorJsonObject()
    {
        // Act & Assert
        Assert.False(ParameterConverter.IsSupportedObject(typeof(Action)));
        Assert.False(ParameterConverter.IsJsonObject(typeof(Action)));
    }

    public sealed record TestMessage
    {
        public required string Text { get; init; }

        public string? Title { get; init; }

        public TestLevel Level { get; init; } = TestLevel.Normal;

        public int? Count { get; init; }
    }

    public enum TestLevel
    {
        Normal,
        High
    }

    public sealed class TestEnvelope
    {
        public string? Text { get; set; }

        public TestInner? Inner { get; set; }
    }

    public sealed class TestInner
    {
        public int Value { get; set; }
    }

    public sealed class TestIgnoreMessage
    {
        [JsonIgnore]
        public string? Secret { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }
    }

    public class TestBaseWithMember
    {
        public int Value { get; set; }
    }

    public sealed class TestDerivedWithHiddenMember : TestBaseWithMember
    {
        public new string Value { get; set; } = string.Empty;
    }

    public class TestBaseWithIgnoredMember
    {
        public int Code { get; set; }
    }

    // The hiding member has a different type than the base member so that GetProperties returns both (see
    // TestDerivedWithHiddenMember above); a same-type "new" hide collapses to a single PropertyInfo and would not
    // exercise the filter-after-grouping behavior this type is meant to test.
    public sealed class TestDerivedHidingWithIgnoredMember : TestBaseWithIgnoredMember
    {
        [JsonIgnore]
        public new string Code { get; set; } = string.Empty;
    }

    public sealed class TestRenamedMessage
    {
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class TestDoubleMessage
    {
        public double Value { get; set; }
    }

    public sealed class TestThrowingMessage
    {
        private string _text = string.Empty;

        public string Text
        {
            get => _text;
            set => _text = value.Length > 0 ? value : throw new ArgumentException("Text must not be empty");
        }
    }
}
