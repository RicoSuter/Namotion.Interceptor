using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

/// <summary>
/// A subject is readable through any parent with a readable path, so hiding one link hides only that link, and a reference
/// to a subject that is not readable reads as null.
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

    // Search leaves out the root, so the house is held by a group.
    private static (TestMixedGroup Root, TestHouse House, TestDevice Device) CreateHouseInGroup()
    {
        var (house, device) = CreateHouse();
        var root = new TestMixedGroup(((IInterceptorSubject)house).Context);
        root.Items = new Dictionary<string, IInterceptorSubject> { ["house"] = house };
        return (root, house, device);
    }

    [Theory]
    [InlineData(false, "Items[house].Private")]
    [InlineData(true, "Items[house].Public")]
    public async Task WhenTheHelperWritesThePathOfASubject_ThenItIsThePathSearchLists(bool hidesFirstParent, string expectedPath)
    {
        // Arrange
        var (root, _, device) = CreateHouseInGroup();
        var configuration = CreateConfiguration(hidesFirstParent ? TestAccessPolicy.DenyRead(nameof(TestHouse.Private)) : null);

        // Act
        var path = configuration.TryGetSubjectPath(root.TryGetRegisteredSubject()!, device.TryGetRegisteredSubject()!);
        var search = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Equal([expectedPath], GetResultPaths(search));
    }

    [Theory]
    [InlineData(nameof(TestHouse.Private), nameof(TestHouse.Public))]
    [InlineData(nameof(TestDevice.DeviceName), nameof(TestDevice.IsOn))]
    public async Task WhenThePolicyDoesNotShowASubject_ThenTheHelperWritesNoPathAndSearchDoesNotListIt(params string[] hiddenMembers)
    {
        // Arrange
        // Hiding both parents leaves no readable path; hiding every property of the device makes it empty for the policy.
        var (root, _, device) = CreateHouseInGroup();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(hiddenMembers));

        // Act
        var path = configuration.TryGetSubjectPath(root.TryGetRegisteredSubject()!, device.TryGetRegisteredSubject()!);
        var search = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.Null(path);
        Assert.Empty(GetResultPaths(search));
    }

    [Fact]
    public async Task WhenASubjectIsOfAnExcludedType_ThenTheHelperWritesNoPathAndSearchDoesNotListIt()
    {
        // Arrange
        var (root, house, device) = CreateHouseInGroup();
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            ExcludeTypes = [typeof(TestDevice)]
        };

        // Act
        var devicePath = configuration.TryGetSubjectPath(registeredRoot, device.TryGetRegisteredSubject()!);
        var housePath = configuration.TryGetSubjectPath(registeredRoot, house.TryGetRegisteredSubject()!);
        var search = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json" });

        // Assert
        Assert.Null(devicePath);
        Assert.Equal("Items[house]", housePath);
        Assert.Equal(["Items[house]"], GetResultPaths(search));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenTheHelperWritesThePathOfASubject_ThenTryGetReadableSubjectResolvesItBack(bool hidesFirstParent)
    {
        // Arrange
        var (root, house, device) = CreateHouseInGroup();
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(hidesFirstParent ? TestAccessPolicy.DenyRead(nameof(TestHouse.Private)) : null);

        // Act
        var housePath = configuration.TryGetSubjectPath(registeredRoot, house.TryGetRegisteredSubject()!);
        var devicePath = configuration.TryGetSubjectPath(registeredRoot, device.TryGetRegisteredSubject()!);

        // Assert
        Assert.Same(house, configuration.TryGetReadableSubject(registeredRoot, housePath!)!.Subject);
        Assert.Same(device, configuration.TryGetReadableSubject(registeredRoot, devicePath!)!.Subject);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("/", "/")]
    public async Task WhenTheHelperWritesThePathOfTheRoot_ThenItIsThePrefixOrNullWhenItIsEmpty(string pathPrefix, string? expectedPath)
    {
        // Arrange
        var (root, _, _) = CreateHouseInGroup();
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            PathPrefix = pathPrefix,
            AccessPolicy = TestAccessPolicy.DenyRead(nameof(TestMixedGroup.Items))
        };

        // Act
        var path = configuration.TryGetSubjectPath(registeredRoot, registeredRoot);
        var search = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json", types = new[] { nameof(TestMixedGroup) } });

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedPath is null ? [] : [expectedPath], GetResultPaths(search));
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
        { "set_property", JsonSerializer.Serialize(new { path = "Items[node].A.Child.Name", value = "Changed" }) },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[node].A.Child.Child", depth = 0 }) },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestNode) }, where = "A.Child.Name == \"G\"" }) }
    };

    [Theory]
    [MemberData(nameof(PassedThroughPartlyHiddenSubjectCalls))]
    public async Task WhenAPathPassesASubjectWhoseOtherLinkIsHidden_ThenTheToolAnswersAsWithoutAPolicy(string toolName, string input)
    {
        // Arrange
        // Each call gets its own graph, so a write of the first does not change what the second sees.
        var (rootWithoutPolicy, gWithoutPolicy) = CreatePassedThroughPartlyHiddenSubject();
        var (rootWithPolicy, gWithPolicy) = CreatePassedThroughPartlyHiddenSubject();
        var request = JsonSerializer.Deserialize<JsonElement>(input);

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(rootWithoutPolicy, CreateConfiguration(policy: null), toolName, request);
        var withPolicy = await McpToolCaller.CallAsync(rootWithPolicy, CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestNode.Secret))), toolName, request);

        // Assert
        Assert.False(withoutPolicy.TryGetProperty("error", out _));
        Assert.DoesNotContain(nameof(TestNode.Secret), withPolicy.GetRawText());
        Assert.Equal(withoutPolicy.GetRawText(), withPolicy.GetRawText());
        Assert.Equal(gWithoutPolicy.Name, gWithPolicy.Name);
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

    // The device got the outside house's Public first, and inside the root it is held only under the hidden Private.
    private static (TestMixedGroup Root, TestDevice Device) CreateHiddenInsideAndHeldOutside()
    {
        var context = CreateContext();
        var device = new TestDevice(context) { DeviceName = "Camera" };
        var outside = new TestHouse(context) { Address = "Outside" };
        outside.Public = device;
        var house = new TestHouse(context) { Address = "Main Street" };
        house.Private = device;
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject> { ["house"] = house };
        return (root, device);
    }

    // The text the answer without a policy contains, so the case reaches the device, or for the where filter the house
    // through it.
    public static TheoryData<string, string, string> HiddenInsideAndHeldOutsideCalls => new()
    {
        { "browse", JsonSerializer.Serialize(new { format = "json", depth = 10, includeProperties = true, includeAttributes = true }), "Camera" },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[house].Private", depth = 0, includeProperties = true }), "Camera" },
        { "search", JsonSerializer.Serialize(new { format = "json", includeProperties = true }), "Camera" },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestDevice) }, includeProperties = true }), "Camera" },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestHouse) }, where = "Private.DeviceName == \"Camera\"" }), "\"Items[house]\"" },
        { "get_property", JsonSerializer.Serialize(new { path = "Items[house].Private.DeviceName" }), "Camera" },
        { "set_property", JsonSerializer.Serialize(new { path = "Items[house].Private.DeviceName", value = "Changed" }), "Camera" }
    };

    [Theory]
    [MemberData(nameof(HiddenInsideAndHeldOutsideCalls))]
    public async Task WhenASubjectsOnlyPathInsideTheRootIsHiddenAndItsOtherParentIsOutside_ThenNoToolRevealsIt(string toolName, string input, string expectedTextWithoutPolicy)
    {
        // Arrange
        var (rootWithoutPolicy, _) = CreateHiddenInsideAndHeldOutside();
        var (rootWithPolicy, device) = CreateHiddenInsideAndHeldOutside();
        var request = JsonSerializer.Deserialize<JsonElement>(input);

        // Act
        var withoutPolicy = await McpToolCaller.CallAsync(rootWithoutPolicy, CreateConfiguration(policy: null), toolName == "set_property" ? "get_property" : toolName, request);
        var withPolicy = await McpToolCaller.CallAsync(rootWithPolicy, CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private))), toolName, request);

        // Assert
        Assert.False(withoutPolicy.TryGetProperty("error", out _));
        Assert.Contains(expectedTextWithoutPolicy, withoutPolicy.GetRawText());
        Assert.DoesNotContain("Camera", withPolicy.GetRawText());
        Assert.Equal("Camera", device.DeviceName);
        if (request.TryGetProperty("path", out var path))
        {
            Assert.Equal($"Path not found: {path.GetString()}", withPolicy.GetProperty("error").GetString());
        }
        else if (withPolicy.TryGetProperty("results", out var results))
        {
            Assert.DoesNotContain(results.EnumerateObject(), entry => entry.Name.Contains("Private") || entry.Name == "Items[house]" && request.TryGetProperty("where", out _));
        }
        else
        {
            Assert.DoesNotContain(nameof(TestHouse.Private), withPolicy.GetRawText());
        }
    }

    [Fact]
    public void WhenASubjectsOnlyPathInsideTheRootIsHiddenAndItsOtherParentIsOutside_ThenTheHelpersAndTheReadabilityHideIt()
    {
        // Arrange
        var (root, device) = CreateHiddenInsideAndHeldOutside();
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestHouse.Private)));

        // Act
        var subject = configuration.TryGetReadableSubject(registeredRoot, "Items[house].Private");
        var property = configuration.TryGetReadableProperty(registeredRoot, "Items[house].Private.DeviceName");
        var isReadable = McpSubjectReadability.For(configuration, root).IsReadableWithPathCheck(device);

        // Assert
        Assert.Null(subject);
        Assert.Null(property);
        Assert.False(isReadable);
    }

    // The device is held by the house's Private, which the provider excludes, and by its Public, which the policy hides.
    private static (TestMixedGroup Root, TestDevice Device) CreateHeldThroughExcludedAndHiddenParents()
    {
        var context = CreateContext();
        var device = new TestDevice(context) { DeviceName = "Camera" };
        var house = new TestHouse(context) { Address = "Main Street" };
        house.Private = device;
        house.Public = device;
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject> { ["house"] = house };
        return (root, device);
    }

    private static McpServerConfiguration CreateExcludingAndHidingConfiguration() => new()
    {
        PathProvider = new ExcludingPathProvider(nameof(TestHouse.Private)),
        AccessPolicy = TestAccessPolicy.DenyRead(nameof(TestHouse.Public)),
        IsReadOnly = false
    };

    // The text the answer without either restriction contains, so the case reaches the device, or for the where filter
    // the house through it.
    public static TheoryData<string, string, string> HeldThroughExcludedAndHiddenParentsCalls => new()
    {
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestDevice) }, includeProperties = true }), "Camera" },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestHouse) }, where = "Public.DeviceName == \"Camera\"" }), "\"Items[house]\"" },
        { "search", JsonSerializer.Serialize(new { format = "json", types = new[] { nameof(TestHouse) }, where = "Private.DeviceName == \"Camera\"" }), "\"Items[house]\"" },
        { "browse", JsonSerializer.Serialize(new { format = "json", depth = 10, includeProperties = true }), "Camera" },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[house].Public", depth = 0, includeProperties = true }), "Camera" },
        { "browse", JsonSerializer.Serialize(new { format = "json", path = "Items[house].Private", depth = 0, includeProperties = true }), "Camera" },
        { "get_property", JsonSerializer.Serialize(new { path = "Items[house].Public.DeviceName" }), "Camera" },
        { "get_property", JsonSerializer.Serialize(new { path = "Items[house].Private.DeviceName" }), "Camera" },
        { "set_property", JsonSerializer.Serialize(new { path = "Items[house].Public.DeviceName", value = "Changed" }), "Camera" },
        { "set_property", JsonSerializer.Serialize(new { path = "Items[house].Private.DeviceName", value = "Changed" }), "Camera" }
    };

    [Theory]
    [MemberData(nameof(HeldThroughExcludedAndHiddenParentsCalls))]
    public async Task WhenTheProviderExcludesOneParentOfASubjectAndThePolicyHidesTheOther_ThenEveryToolReadsItAsMissing(string toolName, string input, string expectedTextWithoutRestrictions)
    {
        // Arrange
        var (rootWithoutRestrictions, _) = CreateHeldThroughExcludedAndHiddenParents();
        var (rootWithRestrictions, device) = CreateHeldThroughExcludedAndHiddenParents();
        var request = JsonSerializer.Deserialize<JsonElement>(input);

        // Act
        var withoutRestrictions = await McpToolCaller.CallAsync(rootWithoutRestrictions, CreateConfiguration(policy: null), toolName == "set_property" ? "get_property" : toolName, request);
        var withRestrictions = await McpToolCaller.CallAsync(rootWithRestrictions, CreateExcludingAndHidingConfiguration(), toolName, request);

        // Assert
        Assert.False(withoutRestrictions.TryGetProperty("error", out _));
        Assert.Contains(expectedTextWithoutRestrictions, withoutRestrictions.GetRawText());
        Assert.DoesNotContain("Camera", withRestrictions.GetRawText());
        Assert.Equal("Camera", device.DeviceName);
        if (request.TryGetProperty("path", out var path))
        {
            Assert.Equal($"Path not found: {path.GetString()}", withRestrictions.GetProperty("error").GetString());
        }
        else if (withRestrictions.TryGetProperty("results", out var results))
        {
            Assert.Empty(results.EnumerateObject());
        }
        else
        {
            Assert.DoesNotContain(nameof(TestHouse.Private), withRestrictions.GetRawText());
            Assert.DoesNotContain(nameof(TestHouse.Public), withRestrictions.GetRawText());
        }
    }

    [Fact]
    public void WhenTheProviderExcludesOneParentOfASubjectAndThePolicyHidesTheOther_ThenTheHelpersAndTheReadabilityHideIt()
    {
        // Arrange
        var (root, device) = CreateHeldThroughExcludedAndHiddenParents();
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var configuration = CreateExcludingAndHidingConfiguration();

        // Act
        var throughPublic = configuration.TryGetReadableSubject(registeredRoot, "Items[house].Public");
        var throughPrivate = configuration.TryGetReadableSubject(registeredRoot, "Items[house].Private");
        var property = configuration.TryGetReadableProperty(registeredRoot, "Items[house].Public.DeviceName");
        var isReadable = McpSubjectReadability.For(configuration, root).IsReadableWithPathCheck(device);

        // Assert
        Assert.Null(throughPublic);
        Assert.Null(throughPrivate);
        Assert.Null(property);
        Assert.False(isReadable);
    }

    [Fact]
    public void WhenASubjectOtherThanTheRootHasNoParent_ThenItIsNotReadable()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestRoom(context) { Name = "Kitchen" };
        var detached = new TestDevice(context) { DeviceName = "Detached" };
        var configuration = CreateConfiguration(new TestAccessPolicy((_, _) => true));

        // Act
        var isReadable = McpSubjectReadability.For(configuration, root).IsReadableWithPathCheck(detached);
        var isRootReadable = McpSubjectReadability.For(configuration, root).IsReadableWithPathCheck(root);

        // Assert
        Assert.Empty(detached.TryGetRegisteredSubject()!.Parents);
        Assert.False(isReadable);
        Assert.True(isRootReadable);
    }

    [Theory]
    [InlineData("Device != null", new[] { "Items[readable]" })]
    [InlineData("Device == null", new[] { "Items[hidden]", "Items[none]" })]
    public async Task WhenAReferenceHoldsASubjectThatIsNotReadable_ThenAWhereFilterReadsItAsNull(string where, string[] expectedPaths)
    {
        // Arrange
        var context = CreateContext();
        var hiddenDevice = new TestDevice(context) { DeviceName = "Hidden" };
        var root = new TestMixedGroup(context);
        root.Items = new Dictionary<string, IInterceptorSubject>
        {
            ["readable"] = new TestRoom(context) { Name = "Readable", Device = new TestDevice(context) { DeviceName = "Readable" } },
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
    public async Task WhenTheOnlyChildOfAnotherTypeIsHidden_ThenTheCollapsedItemTypeIsThatOfTheReadableChildren()
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

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            segment = property.Name != excludedPropertyName ? property.BrowseName : null;
            return segment is not null;
        }
    }
}
