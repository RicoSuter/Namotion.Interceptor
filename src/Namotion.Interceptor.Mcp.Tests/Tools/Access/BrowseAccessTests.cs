using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class BrowseAccessTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static TestRoom CreateRoom()
    {
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        return room;
    }

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy
    };

    private static Task<JsonElement> BrowseAsync(IInterceptorSubject root, IMcpAccessPolicy? policy, object input) =>
        McpToolCaller.CallAsync(root, CreateConfiguration(policy), "browse", input);

    [Fact]
    public async Task WhenThePolicyDeniesReadOnAScalarProperty_ThenBrowseLeavesItOut()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var result = await BrowseAsync(room, TestAccessPolicy.DenyRead(nameof(TestRoom.Temperature)),
            new { format = "json", depth = 0, includeProperties = true });

        // Assert
        var properties = result.GetProperty("result").GetProperty("properties");
        Assert.True(properties.TryGetProperty(nameof(TestRoom.Name), out _));
        Assert.False(properties.TryGetProperty(nameof(TestRoom.Temperature), out _));
    }

    [Fact]
    public async Task WhenThePolicyDeniesReadOnAPropertyHoldingASubject_ThenBrowseLeavesTheBranchOut()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var result = await BrowseAsync(room, TestAccessPolicy.DenyRead(nameof(TestRoom.Device)),
            new { format = "json", depth = 2, includeProperties = true });

        // Assert
        var properties = result.GetProperty("result").GetProperty("properties");
        Assert.False(properties.TryGetProperty(nameof(TestRoom.Device), out _));
        Assert.Equal(0, result.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task WhenTheRequestedPathIsHidden_ThenBrowseReportsItAsNotFound()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var result = await BrowseAsync(room, TestAccessPolicy.DenyRead(nameof(TestRoom.Device)),
            new { format = "json", path = "Device", depth = 0 });

        // Assert
        Assert.Equal("Path not found: Device", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenASubjectHasNoReadableProperty_ThenBrowseLeavesItOut()
    {
        // Arrange
        var room = CreateRoom();
        var policy = TestAccessPolicy.DenyRead(nameof(TestDevice.DeviceName), nameof(TestDevice.IsOn));

        // Act
        var result = await BrowseAsync(room, policy, new { format = "json", depth = 1 });
        var collapsed = await BrowseAsync(room, policy, new { format = "json", depth = 0 });

        // Assert
        Assert.False(result.GetProperty("result").GetProperty("properties").TryGetProperty(nameof(TestRoom.Device), out _));
        Assert.False(collapsed.GetProperty("result").GetProperty("properties").TryGetProperty(nameof(TestRoom.Device), out _));
    }

    [Fact]
    public async Task WhenChildrenAreCollapsed_ThenTheCountOnlyCountsShownChildren()
    {
        // Arrange
        var context = CreateContext();
        var group = new TestDeviceGroup(context);
        var hidden = new TestDevice(context) { DeviceName = "Hidden" };
        group.Devices = new Dictionary<string, TestDevice>
        {
            ["a"] = new(context) { DeviceName = "A" },
            ["b"] = hidden,
            ["c"] = new(context) { DeviceName = "C" }
        };
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || member.Parent.Subject != hidden);

        // Act
        var result = await BrowseAsync(group, policy, new { format = "json", depth = 0 });

        // Assert
        var devices = result.GetProperty("result").GetProperty("properties").GetProperty(nameof(TestDeviceGroup.Devices));
        Assert.Equal(2, devices.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task WhenChildrenAreExpanded_ThenHiddenChildrenAreLeftOut()
    {
        // Arrange
        var context = CreateContext();
        var group = new TestDeviceGroup(context);
        var hidden = new TestDevice(context) { DeviceName = "Hidden" };
        group.Devices = new Dictionary<string, TestDevice>
        {
            ["a"] = new(context) { DeviceName = "A" },
            ["b"] = hidden
        };
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || member.Parent.Subject != hidden);

        // Act
        var result = await BrowseAsync(group, policy, new { format = "json", depth = 1 });

        // Assert
        var children = result.GetProperty("result").GetProperty("properties")
            .GetProperty(nameof(TestDeviceGroup.Devices)).GetProperty("children");
        Assert.True(children.TryGetProperty("a", out _));
        Assert.False(children.TryGetProperty("b", out _));
        Assert.Equal(1, result.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task WhenTheTreeIsTruncated_ThenTheOmittedCountOnlyCountsShownChildren()
    {
        // Arrange
        var context = CreateContext();
        var group = new TestDeviceGroup(context);
        var hidden = new TestDevice(context) { DeviceName = "Hidden" };
        group.Devices = new Dictionary<string, TestDevice>
        {
            ["a"] = new(context) { DeviceName = "A" },
            ["b"] = hidden,
            ["c"] = new(context) { DeviceName = "C" },
            ["d"] = new(context) { DeviceName = "D" }
        };
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || member.Parent.Subject != hidden);
        var input = new { format = "json", depth = 1, maxSubjects = 1 };

        // Act
        var withPolicy = await BrowseAsync(group, policy, input);
        var withoutPolicy = await BrowseAsync(group, policy: null, input);

        // Assert
        Assert.Equal(1, withPolicy.GetProperty("subjectCount").GetInt32());
        Assert.True(withPolicy.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, withPolicy.GetProperty("omittedCount").GetInt32());
        Assert.Equal(1, withoutPolicy.GetProperty("subjectCount").GetInt32());
        Assert.Equal(3, withoutPolicy.GetProperty("omittedCount").GetInt32());
    }

    [Fact]
    public async Task WhenASubjectIsWrittenThroughAHiddenParent_ThenBrowseLeavesItOutAlsoUnderAReadableParent()
    {
        // Arrange
        var context = CreateContext();
        var house = new TestHouse(context) { Address = "Main Street" };
        var device = new TestDevice(context) { DeviceName = "Camera" };
        house.Private = device;
        house.Public = device;

        // Act
        var result = await BrowseAsync(house, TestAccessPolicy.DenyRead(nameof(TestHouse.Private)),
            new { format = "json", depth = 1, includeProperties = true });

        // Assert
        var properties = result.GetProperty("result").GetProperty("properties");
        Assert.False(properties.TryGetProperty(nameof(TestHouse.Private), out _));
        Assert.False(properties.TryGetProperty(nameof(TestHouse.Public), out _));
        Assert.DoesNotContain("Private", result.GetRawText());
    }

    [Fact]
    public async Task WhenThePolicyDeniesReadOnAnAttribute_ThenBrowseLeavesItOut()
    {
        // Arrange
        var room = CreateRoom();
        var temperature = room.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoom.Temperature))!;
        temperature.AddAttribute<string>("Unit", _ => "Celsius");
        temperature.AddAttribute<string>("Source", _ => "Sensor");
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || !(member.IsAttribute && member.BrowseName == "Source"));

        // Act
        var result = await BrowseAsync(room, policy, new { format = "json", depth = 0, includeProperties = true, includeAttributes = true });

        // Assert
        var attributes = result.GetProperty("result").GetProperty("properties")
            .GetProperty(nameof(TestRoom.Temperature)).GetProperty("attributes");
        var names = attributes.EnumerateArray().Select(attribute => attribute.GetProperty("name").GetString()).ToArray();
        Assert.Contains("Unit", names);
        Assert.DoesNotContain("Source", names);
    }

    [Fact]
    public async Task WhenTheRootHasNoReadableProperty_ThenBrowseStillShowsTheRoot()
    {
        // Arrange
        var room = CreateRoom();
        var policy = new TestAccessPolicy((_, action) => action != McpAccessActions.Read);

        // Act
        var result = await BrowseAsync(room, policy, new { format = "json", depth = 1, includeProperties = true });

        // Assert
        Assert.True(result.TryGetProperty("result", out var node));
        Assert.Empty(node.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task WhenASelectedPropertyIsHidden_ThenBrowseWarnsAsForAMissingOne()
    {
        // Arrange
        var room = CreateRoom();

        // Act
        var result = await BrowseAsync(room, TestAccessPolicy.DenyRead(nameof(TestRoom.Temperature)),
            new { format = "json", depth = 0, properties = new[] { nameof(TestRoom.Temperature) } });

        // Assert
        Assert.Contains("No subject in scope has a property named 'Temperature'.",
            result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()));
    }

    [Fact]
    public void WhenThePathOrTheSubjectIsHidden_ThenTryGetReadableSubjectReturnsNull()
    {
        // Arrange
        var room = CreateRoom();
        var root = room.TryGetRegisteredSubject()!;
        var hiddenPath = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestRoom.Device)));
        var noReadableMember = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestDevice.DeviceName), nameof(TestDevice.IsOn)));
        var readable = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestDevice.IsOn)));

        // Act
        var throughHiddenPath = hiddenPath.TryGetReadableSubject(root, "Device");
        var withoutReadableMember = noReadableMember.TryGetReadableSubject(root, "Device");
        var shown = readable.TryGetReadableSubject(root, "Device");

        // Assert
        Assert.Null(throughHiddenPath);
        Assert.Null(withoutReadableMember);
        Assert.Same(room.Device, shown!.Subject);
    }

    [Fact]
    public void WhenNoPolicyIsSet_ThenTryGetReadableSubjectResolvesLikeThePathProvider()
    {
        // Arrange
        var room = CreateRoom();
        var root = room.TryGetRegisteredSubject()!;

        // Act
        var result = CreateConfiguration(policy: null).TryGetReadableSubject(root, "Device");

        // Assert
        Assert.Same(DefaultPathProvider.Instance.TryGetSubjectFromPath(root, "Device"), result);
    }

    [Fact]
    public void WhenThePathEndsInADictionaryKey_ThenTryGetReadableSubjectResolvesTheEntry()
    {
        // Arrange
        var context = CreateContext();
        var group = new TestDeviceGroup(context);
        var hidden = new TestDevice(context) { DeviceName = "Hidden" };
        group.Devices = new Dictionary<string, TestDevice>
        {
            ["a"] = new(context) { DeviceName = "A" },
            ["b"] = hidden
        };
        var root = group.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || member.Parent.Subject != hidden));

        // Act
        var shown = configuration.TryGetReadableSubject(root, "Devices[a]");
        var withoutReadableMember = configuration.TryGetReadableSubject(root, "Devices[b]");
        var missing = configuration.TryGetReadableSubject(root, "Devices[c]");

        // Assert
        Assert.Same(group.Devices["a"], shown!.Subject);
        Assert.Null(withoutReadableMember);
        Assert.Null(missing);
    }

    [Fact]
    public async Task WhenHiddenItemsPrecedeAShownOneInACollection_ThenTheShownOneKeepsItsPosition()
    {
        // Arrange
        // Paths keep the collection position so that they resolve back, so it counts the hidden items before.
        var context = CreateContext();
        var list = new TestDeviceList(context);
        var hidden = new TestDevice[] { new(context) { DeviceName = "Hidden1" }, new(context) { DeviceName = "Hidden2" } };
        list.Devices = [hidden[0], hidden[1], new TestDevice(context) { DeviceName = "Shown" }];
        var policy = new TestAccessPolicy((member, action) =>
            action != McpAccessActions.Read || !hidden.Contains(member.Parent.Subject));

        // Act
        var browse = await BrowseAsync(list, policy, new { format = "json", depth = 1 });
        var search = await McpToolCaller.CallAsync(list, CreateConfiguration(policy), "search",
            new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        var children = browse.GetProperty("result").GetProperty("properties").GetProperty(nameof(TestDeviceList.Devices)).GetProperty("children");
        Assert.Equal(["Devices[2]"], children.EnumerateArray().Select(child => child.GetProperty("$path").GetString()));
        Assert.Equal(["Devices[2]"], search.GetProperty("results").EnumerateObject().Select(entry => entry.Name));
    }
}
