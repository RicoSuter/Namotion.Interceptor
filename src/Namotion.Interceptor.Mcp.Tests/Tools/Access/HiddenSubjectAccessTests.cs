using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

/// <summary>
/// A subject is shown through any parent with a readable path, so hiding one link hides only that link, and a reference
/// to a subject that is not shown reads as null.
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

    // The device got Private first, so without a policy its path goes through Private; hiding Private leaves Public.
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
    public async Task WhenOneParentOfASubjectIsHidden_ThenGetPropertyAndSetPropertyReachItThroughTheOther()
    {
        // Arrange
        var (house, device) = CreateHouse();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private)));

        // Act
        var read = await McpToolCaller.CallAsync(house, configuration, "get_property", new { path = "Public.DeviceName" });
        var write = await McpToolCaller.CallAsync(house, configuration, "set_property", new { path = "Public.DeviceName", value = "Changed" });
        var hidden = await McpToolCaller.CallAsync(house, configuration, "get_property", new { path = "Private.DeviceName" });

        // Assert
        Assert.Equal("Camera", read.GetProperty("value").GetString());
        Assert.True(write.GetProperty("success").GetBoolean());
        Assert.Equal("Changed", device.DeviceName);
        Assert.Equal("Path not found: Private.DeviceName", hidden.GetProperty("error").GetString());
    }

    [Fact]
    public void WhenOneParentOfASubjectIsHidden_ThenTheHelpersResolveItThroughTheOther()
    {
        // Arrange
        var (house, device) = CreateHouse();
        var root = house.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private)));

        // Act
        var subject = configuration.TryGetReadableSubject(root, "Public");
        var property = configuration.TryGetReadableProperty(root, "Public.DeviceName");
        var hiddenSubject = configuration.TryGetReadableSubject(root, "Private");

        // Assert
        Assert.Same(device, subject!.Subject);
        Assert.Equal(nameof(TestDevice.DeviceName), property!.Value.Property.Name);
        Assert.Null(hiddenSubject);
    }

    [Fact]
    public async Task WhenOneParentOfASubjectIsHidden_ThenAWherePathThroughTheOtherReadsIt()
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
        Assert.Equal(["Items[house]"], GetResultPaths(withPolicy));
    }

    // X got Secret first and is also held by A. G is held by B and by X.Child; H is G's child. A path through A.Child
    // passes X, whose other link is hidden, on the way to G.
    private static (TestMixedGroup Root, TestNode G) CreatePassedThroughPartlyHiddenSubject()
    {
        var context = CreateContext();
        var node = new TestNode(context) { Name = "Node" };
        var x = new TestNode(context) { Name = "X" };
        var g = new TestNode(context) { Name = "G" };
        node.Secret = x;
        node.A = x;
        node.B = g;
        x.Child = g;
        g.Child = new TestNode(context) { Name = "H" };
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject> { ["node"] = node };
        return (root, g);
    }

    public static TheoryData<string, string> PassedThroughPartlyHiddenSubjectCalls => new()
    {
        { "get_property", JsonSerializer.Serialize(new { path = "Items[node].A.Child.Name" }) },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[node].A.Child.Child", depth = 0 }) },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestNode) }, where = "A.Child.Name == \"G\"" }) }
    };

    [Theory]
    [MemberData(nameof(PassedThroughPartlyHiddenSubjectCalls))]
    public async Task WhenAPathPassesASubjectWhoseOtherLinkIsHidden_ThenTheToolAnswersAsWithoutAPolicy(string toolName, string input)
    {
        // Arrange
        var (root, _) = CreatePassedThroughPartlyHiddenSubject();
        var request = JsonSerializer.Deserialize<JsonElement>(input);

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(root, CreateConfiguration(policy: null), toolName, request);
        var withPolicy = await McpToolCaller.CallAsync(root, CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestNode.Secret))), toolName, request);

        // Assert
        Assert.False(withoutPolicy.TryGetProperty("error", out _));
        Assert.DoesNotContain(nameof(TestNode.Secret), withPolicy.GetRawText());
        Assert.Equal(withoutPolicy.GetRawText(), withPolicy.GetRawText());
    }

    [Fact]
    public void WhenAPathPassesASubjectWhoseOtherLinkIsHidden_ThenTheHelpersResolveIt()
    {
        // Arrange
        var (root, g) = CreatePassedThroughPartlyHiddenSubject();
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestNode.Secret)));

        // Act
        var property = configuration.TryGetReadableProperty(registeredRoot, "Items[node].A.Child.Name");
        var subject = configuration.TryGetReadableSubject(registeredRoot, "Items[node].A.Child");
        var hidden = configuration.TryGetReadableSubject(registeredRoot, "Items[node].Secret.Child");

        // Assert
        Assert.Same(g, property!.Value.Property.Subject);
        Assert.Same(g, subject!.Subject);
        Assert.Null(hidden);
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

    [Fact]
    public async Task WhenASubjectIsMovedBehindAHiddenPropertyAfterItsCheck_ThenItsPathDoesNotNameTheHiddenProperty()
    {
        // Arrange
        // The root holds A through A, and A holds S. Once the policy is asked about S, its check walks the chain
        // through the root's A; on that first chain property the policy moves A behind the hidden Secret.
        var context = CreateContext();
        var root = new TestNode(context) { Name = "Root" };
        var a = new TestNode(context) { Name = "A" };
        var s = new TestNode(context) { Name = "S" };
        root.A = a;
        a.Child = s;

        var isSubjectChecked = false;
        var isMoved = false;
        var policy = new TestAccessPolicy((member, action) =>
        {
            if (member.Parent.Subject == s)
            {
                isSubjectChecked = true;
            }
            else if (isSubjectChecked && !isMoved && member.Parent.Subject == root && member.Name == nameof(TestNode.A))
            {
                isMoved = true;
                root.Secret = a;
                root.A = null;
            }

            return action != McpAccessActions.Read || member.Name != nameof(TestNode.Secret);
        });

        // Act
        var result = await McpToolCaller.CallAsync(root, CreateConfiguration(policy), "browse", new { format = "json", depth = 2 });

        // Assert
        Assert.True(isMoved);
        Assert.DoesNotContain(nameof(TestNode.Secret), result.GetRawText());
    }
}
