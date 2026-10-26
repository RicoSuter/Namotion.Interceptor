using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class PropertyReadAccessTests
{
    private static TestRoom CreateRoom()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!
            .AddAttribute<string>("Unit", _ => "Celsius");
        room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!
            .AddAttribute<string>("Source", _ => "Sensor");
        return room;
    }

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy,
        IsReadOnly = false
    };

    [Fact]
    public async Task WhenThePolicyDeniesReadOnAProperty_ThenGetPropertyReportsItAsNotFound()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestRoom.Temperature)));

        // Act
        var hidden = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Temperature" });
        var missing = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Pressure" });

        // Assert
        Assert.Equal("Path not found: Temperature", hidden.GetProperty("error").GetString());
        Assert.Equal("Path not found: Pressure", missing.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenThePolicyDeniesReadOnAPropertyOnThePath_ThenGetPropertyReportsThePathAsNotFound()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestRoom.Device)));

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Device.DeviceName" });

        // Assert
        Assert.Equal("Path not found: Device.DeviceName", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenThePolicyDeniesReadOnAnAttribute_ThenGetPropertyLeavesItOut()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || !(member.IsAttribute && member.BrowseName == "Source")));

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Temperature" });

        // Assert
        var attributes = result.GetProperty("attributes");
        Assert.Equal("Celsius", attributes.GetProperty("Unit").GetString());
        Assert.False(attributes.TryGetProperty("Source", out _));
    }

    [Fact]
    public async Task WhenThePolicyDeniesRead_ThenSetPropertyReportsItAsNotFoundAndWritesNothing()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestRoom.Temperature)));

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 30.0 });

        // Assert
        Assert.Equal("Path not found: Temperature", result.GetProperty("error").GetString());
        Assert.Equal(21.5m, room.Temperature);
    }

    [Fact]
    public void WhenAPropertyOnThePathIsHidden_ThenTryGetReadablePropertyReturnsNull()
    {
        // Arrange
        var room = CreateRoom();
        var root = room.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestRoom.Device)));

        // Act
        var hidden = configuration.TryGetReadableProperty(root, "Device.DeviceName");
        var readable = configuration.TryGetReadableProperty(root, "Name");

        // Assert
        Assert.Null(hidden);
        Assert.Equal(nameof(TestRoom.Name), readable!.Value.Property.Name);
    }

    [Fact]
    public void WhenNoPolicyIsSet_ThenTryGetReadablePropertyResolvesLikeThePathProvider()
    {
        // Arrange
        var room = CreateRoom();
        var root = room.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(policy: null);

        // Act
        var result = configuration.TryGetReadableProperty(root, "Device.DeviceName");

        // Assert
        Assert.Equal(DefaultPathProvider.Instance.TryGetPropertyFromPath(root, "Device.DeviceName"), result);
    }

    [Fact]
    public async Task WhenThePathProviderDoesNotIncludeAttributes_ThenGetPropertyStillListsTheReadableOnes()
    {
        // Arrange
        var room = CreateRoom();
        var configuration = new McpServerConfiguration
        {
            PathProvider = new PropertiesWithoutAttributesPathProvider(),
            AccessPolicy = new TestAccessPolicy((_, _) => true),
            IsReadOnly = false
        };

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "get_property", new { path = "Temperature" });

        // Assert
        var attributes = result.GetProperty("attributes");
        Assert.Equal("Celsius", attributes.GetProperty("Unit").GetString());
        Assert.Equal("Sensor", attributes.GetProperty("Source").GetString());
    }

    private sealed class PropertiesWithoutAttributesPathProvider : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => !property.IsAttribute;
    }
}
