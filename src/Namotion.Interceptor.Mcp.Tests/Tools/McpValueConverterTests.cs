using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpValueConverterTests
{
    public static TheoryData<string, Type, object?> StringValues() => new()
    {
        { "\"00:00:30\"", typeof(TimeSpan), TimeSpan.FromSeconds(30) },
        { "\"1.02:03:04\"", typeof(TimeSpan?), new TimeSpan(1, 2, 3, 4) },
        { "\"2026-10-09T12:30:00\"", typeof(DateTime), new DateTime(2026, 10, 9, 12, 30, 0) },
        { "\"2026-10-09T12:30:00+02:00\"", typeof(DateTimeOffset), new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.FromHours(2)) },
        { "\"2026-10-09\"", typeof(DateOnly), new DateOnly(2026, 10, 9) },
        { "\"07:30:00\"", typeof(TimeOnly), new TimeOnly(7, 30) },
        { "\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"", typeof(Guid), new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff") },
        { "\"http://example.com/a\"", typeof(Uri), new Uri("http://example.com/a") },
        { "\"Tuesday\"", typeof(DayOfWeek), DayOfWeek.Tuesday },
        { "\"tuesday\"", typeof(DayOfWeek?), DayOfWeek.Tuesday },
        { "2", typeof(DayOfWeek), DayOfWeek.Tuesday },
        { "\"0.5\"", typeof(decimal), 0.5m },
        { "\"true\"", typeof(bool), true },
        { "\"text\"", typeof(string), "text" },
        { "null", typeof(DayOfWeek?), null },
        { "null", typeof(string), null }
    };

    [Theory]
    [MemberData(nameof(StringValues))]
    public void WhenValueIsInTheListedFormat_ThenItIsConverted(string json, Type type, object? expected)
    {
        // Arrange
        var element = JsonDocument.Parse(json).RootElement;

        // Act
        var value = McpValueConverter.Deserialize(element, type);

        // Assert
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"42\"")]
    [InlineData("\"1\"")]
    [InlineData("\"Monday, Tuesday\"")]
    [InlineData("\"Someday\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void WhenEnumValueIsNotADefinedNameOrNumber_ThenThrowsNamingTheAllowedValues(string json)
    {
        // Arrange
        var element = JsonDocument.Parse(json).RootElement;

        // Act & Assert
        var exception = Assert.Throws<JsonException>(() => McpValueConverter.Deserialize(element, typeof(DayOfWeek)));
        Assert.Contains("Sunday, Monday, Tuesday", exception.Message);
    }

    [Theory]
    [InlineData("\"Read, Write\"", TestPermissions.Read | TestPermissions.Write)]
    [InlineData("3", TestPermissions.Read | TestPermissions.Write)]
    public void WhenFlagsEnumValueCombinesNames_ThenItIsConverted(string json, TestPermissions expected)
    {
        // Arrange
        var element = JsonDocument.Parse(json).RootElement;

        // Act
        var value = McpValueConverter.Deserialize(element, typeof(TestPermissions));

        // Assert
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("\"half\"", typeof(decimal))]
    [InlineData("\"tomorrow\"", typeof(DateTime))]
    [InlineData("\"90 seconds\"", typeof(TimeSpan))]
    [InlineData("null", typeof(int))]
    public void WhenValueCannotBeConverted_ThenThrowsJsonException(string json, Type type)
    {
        // Arrange
        var element = JsonDocument.Parse(json).RootElement;

        // Act & Assert
        Assert.Throws<JsonException>(() => McpValueConverter.Deserialize(element, type));
    }

    [Flags]
    public enum TestPermissions
    {
        None = 0,
        Read = 1,
        Write = 2
    }
}
