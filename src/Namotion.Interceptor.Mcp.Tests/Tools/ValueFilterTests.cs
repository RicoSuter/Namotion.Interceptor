using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tests.Tools.Access;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class ValueFilterTests
{
    [Fact]
    public async Task WhenAValueFilterIsConfigured_ThenGetPropertyReturnsTheFilteredValueAndAttributes()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var json = await McpToolCaller.CallAsync(room, CreateConfiguration(), "get_property", new { path = "Temperature" });

        // Assert
        Assert.Equal(0m, json.GetProperty("value").GetDecimal());
        Assert.Equal("hidden", json.GetProperty("attributes").GetProperty("Unit").GetString());
    }

    [Fact]
    public async Task WhenAValueFilterIsConfigured_ThenBrowseListsTheFilteredValueAndAttributes()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var json = await McpToolCaller.CallAsync(room, CreateConfiguration(), "browse",
            new { format = "json", depth = 0, includeProperties = true, includeAttributes = true });

        // Assert
        var temperature = json.GetProperty("result").GetProperty("properties").GetProperty("Temperature");
        Assert.Equal(0m, temperature.GetProperty("value").GetDecimal());
        Assert.Equal("hidden", temperature.GetProperty("attributes").EnumerateArray().Single().GetProperty("value").GetString());
    }

    [Fact]
    public async Task WhenAValueFilterIsConfigured_ThenSearchFiltersOnAndListsTheFilteredValue()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var json = await McpToolCaller.CallAsync(room, CreateConfiguration(), "search",
            new { format = "json", where = "IsOn == false", properties = new[] { "IsOn" } });

        // Assert
        var device = Assert.Single(json.GetProperty("results").EnumerateObject()).Value;
        Assert.False(device.GetProperty("properties").GetProperty("IsOn").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task WhenAValueFilterIsConfigured_ThenSetPropertyReturnsTheFilteredPreviousValue()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var json = await McpToolCaller.CallAsync(room, CreateConfiguration(), "set_property", new { path = "Temperature", value = 30 });

        // Assert
        Assert.Equal(0m, json.GetProperty("previousValue").GetDecimal());
        Assert.Equal(30m, room.Temperature);
    }

    private static TestRoom CreateRoom()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context)
        {
            Name = "Kitchen",
            Temperature = 21.5m,
            Device = new TestDevice { DeviceName = "Light", IsOn = true }
        };
        room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!.AddAttribute<string>("Unit", _ => "Celsius");
        return room;
    }

    private static McpServerConfiguration CreateConfiguration() => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        IsReadOnly = false,
        ValueFilters = { new TestValueFilter() }
    };

    private sealed class TestValueFilter : IMcpValueFilter
    {
        public object? Filter(RegisteredSubjectProperty member, object? value) => member.Name switch
        {
            nameof(TestRoom.Temperature) => 0m,
            "Temperature@Unit" => "hidden",
            nameof(TestDevice.IsOn) => false,
            _ => value
        };
    }
}
