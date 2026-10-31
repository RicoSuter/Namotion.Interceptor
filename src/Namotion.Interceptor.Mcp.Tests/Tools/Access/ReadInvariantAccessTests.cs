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

    // The text the answer without a policy contains, so the case shows the device.
    public static TheoryData<string, string, string> SubtreeCalls => new()
    {
        { "browse", JsonSerializer.Serialize(new { format = "json", depth = 3, includeProperties = true }), "Light" },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[room].Device", depth = 0, includeProperties = true }), "Light" },
        { "get_property", JsonSerializer.Serialize(new { path = "Items[room].Device.DeviceName" }), "Light" },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestRoom) }, where = "Device.DeviceName == \"Light\"" }), "Items[room]" },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[room]", depth = 1 }), "\"$path\":\"Items[room].Device\"" },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestDevice) } }), "\"Items[room].Device\"" }
    };

    [Theory]
    [MemberData(nameof(SubtreeCalls))]
    public async Task WhenASharedSubjectWasFirstAttachedOutsideTheRoot_ThenADenyWritesPolicyAnswersAsWithoutAPolicy(string toolName, string input, string expectedText)
    {
        // Arrange
        // The device's first parent is outside the tool root. The tools write its path through the first parent from
        // which the registry writes one, the room's Device.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        var outside = new TestRoom(context) { Name = "Outside" };
        outside.Device = device;
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = device;
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject> { ["room"] = room };
        var request = JsonSerializer.Deserialize<JsonElement>(input);
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var configurationWithPolicy = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance, AccessPolicy = DenyAllWrites };

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(root, configuration, toolName, request);
        var withPolicy = await McpToolCaller.CallAsync(root, configurationWithPolicy, toolName, request);

        // Assert
        Assert.False(withoutPolicy.TryGetProperty("error", out _));
        Assert.Contains(expectedText, withoutPolicy.GetRawText());
        Assert.Equal(withoutPolicy.GetRawText(), withPolicy.GetRawText());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task WhenTheWrittenPathOfASubjectWithSeveralParentsDoesNotResolveBack_ThenADenyWritesPolicyBrowsesAsWithoutAPolicy(int depth)
    {
        // Arrange
        // The device is written through Private, whose segment contains the separator, so the text does not resolve.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var house = new TestHouse(context) { Address = "Main Street" };
        var device = new TestDevice(context) { DeviceName = "Camera" };
        house.Private = device;
        house.Public = device;
        var pathProvider = new DottedPrivatePathProvider();
        var request = JsonSerializer.SerializeToElement(new { format = "json", depth, includeProperties = true });

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(house, new McpServerConfiguration { PathProvider = pathProvider }, "browse", request);
        var withPolicy = await McpToolCaller.CallAsync(house, new McpServerConfiguration { PathProvider = pathProvider, AccessPolicy = DenyAllWrites }, "browse", request);

        // Assert
        Assert.Contains("My.Private", withoutPolicy.GetRawText());
        Assert.Equal(withoutPolicy.GetRawText(), withPolicy.GetRawText());
    }

    private sealed class DottedPrivatePathProvider : PathProviderBase
    {
        public override string? TryGetPropertySegment(RegisteredSubjectProperty property) =>
            property.Name == nameof(TestHouse.Private) ? "My.Private" : property.BrowseName;
    }

    private sealed class NameAndSubjectsPathProvider : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
            property.CanContainSubjects || property.Name == nameof(TestRoom.Name);
    }
}
