using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

/// <summary>
/// A policy that allows every read changes nothing about what the tools show or resolve, also when the path provider
/// includes only some properties.
/// </summary>
public class ReadInvariantAccessTests
{
    private static readonly TestAccessPolicy DenyAllWrites = new((_, action) => action != McpAccessActions.Write);

    // The provider includes the room's Name and the properties holding subjects, so every property of the device is
    // excluded by the provider while the device is still reachable.
    private static TestRoom CreateRoom()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Name))!.AddAttribute<string>("Label", _ => "Room");
        return room;
    }

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy) => new()
    {
        PathProvider = new NameAndSubjectsPathProvider(),
        AccessPolicy = policy
    };

    public static TheoryData<string, string> Calls => new()
    {
        { "browse", JsonSerializer.Serialize(new { format = "json", depth = 2, includeProperties = true, includeAttributes = true }) },
        { "browse", JsonSerializer.Serialize(new { format = "json", depth = 0, includeProperties = true }) },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Device", depth = 0, includeProperties = true }) },
        { "search", JsonSerializer.Serialize(new { format = "json", includeProperties = true, includeAttributes = true }) },
        { "get_property", JsonSerializer.Serialize(new { path = "Temperature" }) },
        { "get_property", JsonSerializer.Serialize(new { path = "Device.DeviceName" }) }
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task WhenThePolicyDeniesOnlyWrites_ThenTheToolsAnswerAsWithoutAPolicy(string toolName, string input)
    {
        // Arrange
        var room = CreateRoom();
        var request = JsonSerializer.Deserialize<JsonElement>(input);

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(room, CreateConfiguration(policy: null), toolName, request);
        var withPolicy = await McpToolCaller.CallAsync(room, CreateConfiguration(DenyAllWrites), toolName, request);

        // Assert
        Assert.False(withoutPolicy.TryGetProperty("error", out _));
        Assert.Equal(withoutPolicy.GetRawText(), withPolicy.GetRawText());
    }

    [Fact]
    public void WhenThePolicyDeniesOnlyWrites_ThenTheHelpersResolveAsWithoutAPolicy()
    {
        // Arrange
        var room = CreateRoom();
        var root = room.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(DenyAllWrites);

        // Act
        var property = configuration.TryGetReadableProperty(root, "Device.DeviceName");
        var subject = configuration.TryGetReadableSubject(root, "Device");

        // Assert
        Assert.Equal(nameof(TestDevice.DeviceName), property!.Value.Property.Name);
        Assert.Same(room.Device, subject!.Subject);
    }

    private sealed class NameAndSubjectsPathProvider : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
            property.CanContainSubjects || property.Name == nameof(TestRoom.Name);
    }
}
