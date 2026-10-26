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

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy, bool isReadOnly = false) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy,
        IsReadOnly = isReadOnly
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
    public async Task WhenThePolicyDeniesWrite_ThenGetPropertyAndBrowseDoNotReportThePropertyAsWritable()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyWrite(nameof(TestDevice.IsOn)));

        // Act
        var property = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Device.IsOn" });
        var browse = await McpToolCaller.CallAsync(room, configuration, "browse",
            new { format = "json", path = "Device", depth = 0, includeProperties = true });

        // Assert
        Assert.False(property.TryGetProperty("isWritable", out _));
        var properties = browse.GetProperty("result").GetProperty("properties");
        Assert.False(properties.GetProperty("IsOn").TryGetProperty("isWritable", out _));
        Assert.True(properties.GetProperty("DeviceName").GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task WhenTheServerIsReadOnly_ThenTheReadOnlyErrorComesBeforeThePolicy()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyWrite(nameof(TestRoom.Temperature)), isReadOnly: true);

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 25.0 });

        // Assert
        Assert.Equal("Server is in read-only mode.", result.GetProperty("error").GetString());
    }

    [Fact]
    public void WhenNoPolicyIsSet_ThenIsAllowedIsTrueForEveryAction()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(policy: null);
        var property = room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!;

        // Act
        var canRead = configuration.IsAllowed(property, McpAccessActions.Read);
        var canWrite = configuration.IsAllowed(property, McpAccessActions.Write);
        var canDoAnything = configuration.IsAllowed(property, "execute");

        // Assert
        Assert.True(canRead);
        Assert.True(canWrite);
        Assert.True(canDoAnything);
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
