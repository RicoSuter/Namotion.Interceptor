using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class SearchAccessTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static Task<JsonElement> SearchAsync(IInterceptorSubject root, IMcpAccessPolicy? policy, object input) =>
        McpToolCaller.CallAsync(root, new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            AccessPolicy = policy
        }, "search", input);

    private static string[] GetResultPaths(JsonElement result) =>
        result.GetProperty("results").EnumerateObject().Select(entry => entry.Name).ToArray();

    // A device two levels below the root: Items[hidden] holds a group whose Devices[x] holds the device. The
    // property holding the device is readable; the root's Items is not.
    private static (TestMixedGroup Root, TestDevice Device) CreateNestedDevice()
    {
        var context = CreateContext();
        var root = new TestMixedGroup(context);
        var group = new TestDeviceGroup(context);
        var device = new TestDevice(context) { DeviceName = "Light" };
        group.Devices = new Dictionary<string, TestDevice> { ["x"] = device };
        root.Items = new Dictionary<string, IInterceptorSubject> { ["hidden"] = group };
        return (root, device);
    }

    private static TestAccessPolicy DenyReadOnTheItemsOf(IInterceptorSubject root) =>
        new((member, action) => action != McpAccessActions.Read ||
            !(member.Name == nameof(TestMixedGroup.Items) && member.Parent.Subject == root));

    [Fact]
    public async Task WhenAPropertyOnTheWayFromTheRootIsHidden_ThenSearchLeavesTheSubjectOut()
    {
        // Arrange
        var (root, _) = CreateNestedDevice();

        // Act
        var shown = await SearchAsync(root, policy: null, new { format = "json", types = new[] { nameof(TestDevice) } });
        var hidden = await SearchAsync(root, DenyReadOnTheItemsOf(root), new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.Single(GetResultPaths(shown));
        Assert.Empty(GetResultPaths(hidden));
    }

    [Fact]
    public async Task WhenTheHoldingPropertyIsHidden_ThenSearchLeavesTheSubjectOut()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = new TestDevice(context) { DeviceName = "Light" };

        // Act
        var result = await SearchAsync(room, TestAccessPolicy.DenyRead(nameof(TestRoom.Device)),
            new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.Empty(GetResultPaths(result));
    }

    [Fact]
    public async Task WhenOneParentOfASubjectIsHidden_ThenSearchReturnsItWithAPathThroughTheReadableParent()
    {
        // Arrange
        // The device got Private first, so a path through Private would be written without a policy.
        var context = CreateContext();
        var house = new TestHouse(context) { Address = "Main Street" };
        var device = new TestDevice(context) { DeviceName = "Camera" };
        house.Private = device;
        house.Public = device;

        // Act
        var result = await SearchAsync(house, TestAccessPolicy.DenyRead(nameof(TestHouse.Private)),
            new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.Equal([nameof(TestHouse.Public)], GetResultPaths(result));
        Assert.DoesNotContain("Private", result.GetRawText());
    }

    [Fact]
    public async Task WhenAWherePathIsHidden_ThenItReadsAsMissing()
    {
        // Arrange
        var context = CreateContext();
        var group = new TestDeviceGroup(context);
        group.Devices = new Dictionary<string, TestDevice>
        {
            ["a"] = new(context) { DeviceName = "A", IsOn = true },
            ["b"] = new(context) { DeviceName = "B", IsOn = false }
        };

        // Act
        var result = await SearchAsync(group, TestAccessPolicy.DenyRead(nameof(TestDevice.IsOn)),
            new { format = "json", where = "IsOn == true" });

        // Assert
        Assert.Empty(GetResultPaths(result));
        Assert.Contains("No subject in scope has a property named 'IsOn'.",
            result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()));
    }

    [Fact]
    public async Task WhenAPropertyIsHidden_ThenSearchDoesNotListIt()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        // Act
        var result = await SearchAsync(room, TestAccessPolicy.DenyRead(nameof(TestDevice.IsOn)),
            new { format = "json", types = new[] { nameof(TestDevice) }, includeProperties = true });

        // Assert
        var properties = result.GetProperty("results").EnumerateObject().Single().Value.GetProperty("properties");
        Assert.True(properties.TryGetProperty(nameof(TestDevice.DeviceName), out _));
        Assert.False(properties.TryGetProperty(nameof(TestDevice.IsOn), out _));
    }

    [Fact]
    public async Task WhenOnlyAHiddenSubjectHasASelectedProperty_ThenSearchWarnsAsForAMissingOne()
    {
        // Arrange
        var (root, _) = CreateNestedDevice();

        // Act
        var result = await SearchAsync(root, DenyReadOnTheItemsOf(root),
            new { format = "json", properties = new[] { nameof(TestDevice.DeviceName) } });

        // Assert
        Assert.Contains("No subject in scope has a property named 'DeviceName'.",
            result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()));
    }

    [Fact]
    public async Task WhenASubjectIsMovedWhileSearchChecksIt_ThenItsKeyAndItsPathAreTheSame()
    {
        // Arrange
        // The policy moves A from the root's A to the root's B while it is asked about S, after S's path is written.
        var context = CreateContext();
        var root = new TestNode(context) { Name = "Root" };
        var a = new TestNode(context) { Name = "A" };
        var s = new TestNode(context) { Name = "S" };
        root.A = a;
        a.Child = s;

        var isMoved = false;
        var policy = new TestAccessPolicy((member, _) =>
        {
            if (!isMoved && member.Parent.Subject == s)
            {
                isMoved = true;
                root.B = a;
                root.A = null;
            }

            return true;
        });

        // Act
        var result = await SearchAsync(root, policy, new { format = "json", types = new[] { nameof(TestNode) } });

        // Assert
        Assert.True(isMoved);
        Assert.All(result.GetProperty("results").EnumerateObject(),
            entry => Assert.Equal(entry.Name, entry.Value.GetProperty("$path").GetString()));
    }
}
