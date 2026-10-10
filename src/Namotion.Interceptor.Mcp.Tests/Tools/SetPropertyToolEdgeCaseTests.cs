using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SetPropertyToolEdgeCaseTests
{
    [Fact]
    public async Task WhenPathPointsToSubject_ThenReturnsError()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Device", value = "something" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Contains("subject", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenStringValueForNonStringType_ThenDeserializesCorrectly()
    {
        // Arrange — MCP SDK may send values as strings (e.g., "true" instead of true)
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = false };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act — send boolean as string "true"
        var input = JsonSerializer.SerializeToElement(new { path = "Device.IsOn", value = "true" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(room.Device.IsOn);
    }

    public static TheoryData<string, string, object> StringFormattedValues() => new()
    {
        { "Interval", "00:00:30", TimeSpan.FromSeconds(30) },
        { "CreatedAt", "2026-10-09T12:30:00", new DateTime(2026, 10, 9, 12, 30, 0) },
        { "StartsAt", "2026-10-09T12:30:00+02:00", new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.FromHours(2)) },
        { "Id", "6f9619ff-8b86-d011-b42d-00c04fc964ff", new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff") },
        { "Day", "Tuesday", DayOfWeek.Tuesday },
        { "OptionalDay", "friday", DayOfWeek.Friday }
    };

    [Theory]
    [MemberData(nameof(StringFormattedValues))]
    public async Task WhenStringValueIsInTheListedFormat_ThenPropertyIsSet(string path, string value, object expected)
    {
        // Arrange
        var (schedule, tool) = CreateSchedule();

        // Act
        var result = await tool.Handler(JsonSerializer.SerializeToElement(new { path, value }), CancellationToken.None);

        // Assert
        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
        Assert.Equal(expected, schedule.TryGetRegisteredSubject()!.TryGetProperty(path)!.GetValue());
    }

    [Fact]
    public async Task WhenEnumValueIsUndefined_ThenReturnsErrorAndKeepsTheValue()
    {
        // Arrange
        var (schedule, tool) = CreateSchedule();

        // Act
        var result = await tool.Handler(JsonSerializer.SerializeToElement(new { path = "Day", value = 42 }), CancellationToken.None);

        // Assert
        var error = JsonSerializer.SerializeToElement(result).GetProperty("error").GetString();
        Assert.Contains("Monday", error);
        Assert.Equal(DayOfWeek.Sunday, schedule.Day);
    }

    private static (TestSchedule Schedule, McpToolInfo Tool) CreateSchedule()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var schedule = new TestSchedule(context);
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var tool = new McpToolFactory(schedule, config).CreateTools().First(t => t.Name == "set_property");
        return (schedule, tool);
    }

    [Fact]
    public async Task WhenSettingProperty_ThenPreviousValueIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Original", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Name", value = "Updated" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Original", json.GetProperty("previousValue").GetString());
        Assert.Equal("Updated", room.Name);
    }
}
