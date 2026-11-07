using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class WriteAccessTests
{
    private static TestRoom CreateRoom()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        return room;
    }

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy policy) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy
    };

    [Fact]
    public async Task WhenThePolicyDeniesWrite_ThenSetPropertyRefusesWithThePolicyMessageAndTheValueIsUnchanged()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyWrite(nameof(TestRoom.Temperature)));

        // Act
        var refused = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 25.0 });
        var allowed = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Name", value = "Hall" });

        // Assert
        Assert.Equal("Property is not writable: Temperature. The access policy does not allow it.", refused.GetProperty("error").GetString());
        Assert.Equal(21.5m, room.Temperature);
        Assert.True(allowed.GetProperty("success").GetBoolean());
        Assert.Equal("Hall", room.Name);
    }

    [Fact]
    public async Task WhenThePolicyDeniesReadAndWrite_ThenSetPropertyAnswersAsForAMissingPath()
    {
        // Arrange
        var room = CreateRoom();
        var policy = new TestAccessPolicy((member, _) => member.Name != nameof(TestRoom.Temperature));
        var configuration = CreateConfiguration(policy);

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 25.0 });

        // Assert
        Assert.Equal("Path not found: Temperature", result.GetProperty("error").GetString());
        Assert.Equal(21.5m, room.Temperature);
    }

    [Fact]
    public async Task WhenThePolicyDeniesWrite_ThenGetPropertyAndBrowseDoNotReportThePropertyAsWritable()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyWrite(nameof(TestDevice.IsOn)));

        // Act
        var property = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Device.IsOn" });
        var writableProperty = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Device.DeviceName" });
        var browse = await McpToolCaller.CallAsync(room, configuration, "browse",
            new { format = "json", path = "Device", depth = 0, includeProperties = true });

        // Assert
        Assert.False(property.TryGetProperty("isWritable", out _));
        Assert.True(writableProperty.GetProperty("isWritable").GetBoolean());
        var properties = browse.GetProperty("result").GetProperty("properties");
        Assert.False(properties.GetProperty("IsOn").TryGetProperty("isWritable", out _));
        Assert.True(properties.GetProperty("DeviceName").GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task WhenNoPolicyIsSet_ThenSetPropertyRefusesWithThePolicyMessageAndTheValueIsUnchanged()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 25.0 });

        // Assert
        Assert.Equal("Property is not writable: Temperature. The access policy does not allow it.", result.GetProperty("error").GetString());
        Assert.Equal(21.5m, room.Temperature);
    }

    [Fact]
    public async Task WhenNoPolicyIsSet_ThenTheToolsReadButReportNothingAsWritable()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var property = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Device.IsOn" });
        var browse = await McpToolCaller.CallAsync(room, configuration, "browse",
            new { format = "json", path = "Device", depth = 0, includeProperties = true });

        // Assert
        Assert.True(property.GetProperty("value").GetBoolean());
        Assert.False(property.TryGetProperty("isWritable", out _));
        var properties = browse.GetProperty("result").GetProperty("properties");
        Assert.Equal("Light", properties.GetProperty("DeviceName").GetProperty("value").GetString());
        Assert.False(properties.GetProperty("DeviceName").TryGetProperty("isWritable", out _));
        Assert.False(properties.GetProperty("IsOn").TryGetProperty("isWritable", out _));
    }

    [Fact]
    public void WhenNoPolicyIsSet_ThenTheReadOnlyPolicyAllowsOnlyRead()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var property = room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!;

        // Act
        var canRead = configuration.IsAllowed(property, McpAccessActions.Read);
        var canWrite = configuration.IsAllowed(property, McpAccessActions.Write);
        var canExecute = configuration.IsAllowed(property, "execute");
        var canReadHistory = configuration.IsAllowed(property, "history");
        var canReadInOtherCase = configuration.IsAllowed(property, "READ");

        // Assert
        Assert.Same(ReadOnlyMcpAccessPolicy.Instance, configuration.AccessPolicy);
        Assert.True(canRead);
        Assert.False(canWrite);
        Assert.False(canExecute);
        Assert.False(canReadHistory);
        Assert.False(canReadInOtherCase);
    }

    [Fact]
    public async Task WhenThePolicyAllowsAll_ThenEveryActionIsAllowedAndSetPropertyWrites()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(AllowAllMcpAccessPolicy.Instance);
        var property = room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!;

        // Act
        var canWrite = configuration.IsAllowed(property, McpAccessActions.Write);
        var canExecute = configuration.IsAllowed(property, "execute");
        var result = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 25.0 });

        // Assert
        Assert.True(canWrite);
        Assert.True(canExecute);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(25.0m, room.Temperature);
    }

    [Fact]
    public void WhenThePolicyIsSetToNull_ThenTheConfigurationThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            AccessPolicy = null!
        });
    }

    [Theory]
    [InlineData("get_property")]
    [InlineData("set_property")]
    [InlineData("browse")]
    [InlineData("search")]
    public async Task WhenThePolicyThrows_ThenTheToolCallFails(string toolName)
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(new TestAccessPolicy((_, _) => throw new InvalidOperationException("Policy failed.")));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpToolCaller.CallAsync(room, configuration, toolName, new { path = "Temperature", value = 25.0, format = "json", includeProperties = true }));
        Assert.Equal("Policy failed.", exception.Message);
        Assert.Equal(21.5m, room.Temperature);
    }

    [Fact]
    public void WhenAPolicyIsSet_ThenIsAllowedAsksIt()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(new TestAccessPolicy((_, action) => action == "execute"));
        var property = room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!;

        // Act
        var canExecute = configuration.IsAllowed(property, "execute");
        var canWrite = configuration.IsAllowed(property, McpAccessActions.Write);

        // Assert
        Assert.True(canExecute);
        Assert.False(canWrite);
    }
}
