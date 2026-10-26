using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

/// <summary>
/// A subject the tools do not show is not reachable through another parent, and a reference to it reads as null.
/// </summary>
public class HiddenSubjectAccessTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy, bool isReadOnly = false) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy,
        IsReadOnly = isReadOnly
    };

    // The device is written through Private, the parent it got first, so hiding Private hides the device.
    private static (TestHouse House, TestDevice Device) CreateHouse()
    {
        var context = CreateContext();
        var house = new TestHouse(context) { Address = "Main Street" };
        var device = new TestDevice(context) { DeviceName = "Camera" };
        house.Private = device;
        house.Public = device;
        return (house, device);
    }

    private static string[] GetResultPaths(JsonElement result) =>
        result.GetProperty("results").EnumerateObject().Select(entry => entry.Name).ToArray();

    [Fact]
    public async Task WhenASubjectIsHiddenThroughItsWrittenParent_ThenGetPropertyAndSetPropertyDoNotReachItThroughAnother()
    {
        // Arrange
        var (house, device) = CreateHouse();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private)));

        // Act
        var read = await McpToolCaller.CallAsync(house, configuration, "get_property", new { path = "Public.DeviceName" });
        var write = await McpToolCaller.CallAsync(house, configuration, "set_property", new { path = "Public.DeviceName", value = "Changed" });

        // Assert
        Assert.Equal("Path not found: Public.DeviceName", read.GetProperty("error").GetString());
        Assert.Equal("Path not found: Public.DeviceName", write.GetProperty("error").GetString());
        Assert.Equal("Camera", device.DeviceName);
    }

    [Fact]
    public void WhenASubjectIsHiddenThroughItsWrittenParent_ThenTheHelpersDoNotResolveItThroughAnother()
    {
        // Arrange
        var (house, _) = CreateHouse();
        var root = house.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private)));

        // Act
        var subject = configuration.TryGetReadableSubject(root, "Public");
        var property = configuration.TryGetReadableProperty(root, "Public.DeviceName");
        var holdingProperty = configuration.TryGetReadableProperty(root, "Public");

        // Assert
        Assert.Null(subject);
        Assert.Null(property);
        Assert.Equal(nameof(TestHouse.Public), holdingProperty!.Value.Property.Name);
    }

    [Fact]
    public async Task WhenASubjectIsHiddenThroughItsWrittenParent_ThenAWherePathThroughAnotherReadsAsMissing()
    {
        // Arrange
        // Search leaves out the root, so the house is held by a group.
        var (house, _) = CreateHouse();
        var root = new TestMixedGroup(((IInterceptorSubject)house).Context);
        root.Items = new Dictionary<string, IInterceptorSubject> { ["house"] = house };
        var input = new { format = "json", types = new[] { nameof(TestHouse) }, where = "Public.DeviceName == \"Camera\"" };

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(root, CreateConfiguration(policy: null), "search", input);
        var withPolicy = await McpToolCaller.CallAsync(root, CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private))), "search", input);

        // Assert
        Assert.Equal(["Items[house]"], GetResultPaths(withoutPolicy));
        Assert.Empty(GetResultPaths(withPolicy));
    }

    [Theory]
    [InlineData("Device != null", new[] { "Items[shown]" })]
    [InlineData("Device == null", new[] { "Items[hidden]", "Items[none]" })]
    public async Task WhenAReferenceHoldsASubjectThatIsNotShown_ThenAWhereFilterReadsItAsNull(string where, string[] expectedPaths)
    {
        // Arrange
        var context = CreateContext();
        var hiddenDevice = new TestDevice(context) { DeviceName = "Hidden" };
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject>
        {
            ["shown"] = new TestRoom(context) { Name = "Shown", Device = new TestDevice(context) { DeviceName = "Shown" } },
            ["hidden"] = new TestRoom(context) { Name = "Hidden", Device = hiddenDevice },
            ["none"] = new TestRoom(context) { Name = "None" }
        };
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || member.Parent.Subject != hiddenDevice);

        // Act
        var result = await McpToolCaller.CallAsync(root, CreateConfiguration(policy), "search",
            new { format = "json", types = new[] { nameof(TestRoom) }, where });

        // Assert
        Assert.Equal(expectedPaths, GetResultPaths(result).Order().ToArray());
    }

    [Fact]
    public async Task WhenTheServerIsReadOnly_ThenTheReadOnlyErrorComesBeforeAHiddenPath()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestRoom.Temperature)), isReadOnly: true);

        // Act
        var result = await McpToolCaller.CallAsync(room, configuration, "set_property", new { path = "Temperature", value = 25.0 });

        // Assert
        Assert.Equal("Server is in read-only mode.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTheOnlyChildOfAnotherTypeIsHidden_ThenTheCollapsedItemTypeIsThatOfTheShownChildren()
    {
        // Arrange
        var context = CreateContext();
        var hiddenRoom = new TestRoom(context) { Name = "Hidden" };
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject>
        {
            ["a"] = new TestDevice(context) { DeviceName = "A" },
            ["b"] = hiddenRoom,
            ["c"] = new TestDevice(context) { DeviceName = "C" }
        };
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || member.Parent.Subject != hiddenRoom);

        // Act
        var result = await McpToolCaller.CallAsync(root, CreateConfiguration(policy), "browse", new { format = "json", depth = 0 });

        // Assert
        var items = result.GetProperty("result").GetProperty("properties").GetProperty(nameof(TestMixedGroup.Items));
        Assert.Equal(2, items.GetProperty("count").GetInt32());
        Assert.Equal(nameof(TestDevice), items.GetProperty("itemType").GetString());
    }
}
