using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Mcp.Tests.TestGraphs;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

/// <summary>
/// An attribute that can hold subjects is not listed as a value, so the subjects it holds are not serialized past the
/// policy; such a subject is reached through its own path.
/// </summary>
public class SubjectAttributeAccessTests
{
    // The attribute Spare of the room's Name holds a device, and the room's Device holds another. Search leaves out the
    // root, so the room is held by a group.
    private static TestMixedGroup CreateRoot()
    {
        var context = CreateContext();
        var room = new TestAttributedRoom(context)
        {
            Name = "Kitchen",
            Name_Spare = new TestDevice(context) { DeviceName = "Secret spare", IsOn = true },
            Device = new TestDevice(context) { DeviceName = "Secret lamp", IsOn = true }
        };
        return new TestMixedGroup(context) { Items = new Dictionary<string, IInterceptorSubject> { ["room"] = room } };
    }

    private const string NamePath = "Items[room].Name";

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy ?? ReadOnlyMcpAccessPolicy.Instance
    };

    public static TheoryData<string, string, string[]> Calls()
    {
        var calls = new (string ToolName, object Input)[]
        {
            ("get_property", new { path = NamePath }),
            ("browse", new { format = "json", depth = 2, includeProperties = true, includeAttributes = true }),
            ("browse", new { format = "text", depth = 2, includeProperties = true, includeAttributes = true }),
            ("search", new { format = "json", includeProperties = true, includeAttributes = true }),
            ("search", new { format = "text", includeProperties = true, includeAttributes = true })
        };
        string[][] deniedMembers =
        [
            [nameof(TestDevice.DeviceName)],
            [nameof(TestDevice.DeviceName), nameof(TestDevice.IsOn)]
        ];

        var data = new TheoryData<string, string, string[]>();
        foreach (var (toolName, input) in calls)
        {
            foreach (var members in deniedMembers)
            {
                data.Add(toolName, JsonSerializer.Serialize(input), members);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task WhenAnAttributeHoldsASubjectWhosePropertiesAreHidden_ThenNoToolListsThemThroughTheAttribute(string toolName, string input, string[] deniedMembers)
    {
        // Arrange
        var root = CreateRoot();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(deniedMembers));

        // Act
        var result = await McpToolCaller.CallAsync(root, configuration, toolName, JsonSerializer.Deserialize<JsonElement>(input));

        // Assert
        Assert.False(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out _));
        Assert.DoesNotContain("Secret", result.GetRawText());
    }

    [Fact]
    public async Task WhenNoPolicyIsSet_ThenAnAttributeHoldingASubjectIsNotListedAsAValue()
    {
        // Arrange
        var root = CreateRoot();
        var configuration = CreateConfiguration(policy: null);

        // Act
        var property = await McpToolCaller.CallAsync(root, configuration, "get_property", new { path = NamePath });
        var browse = await McpToolCaller.CallAsync(root, configuration, "browse",
            new { format = "json", path = "Items[room]", depth = 0, includeProperties = true, includeAttributes = true });

        // Assert
        Assert.False(property.TryGetProperty("attributes", out _));
        var name = browse.GetProperty("result").GetProperty("properties").GetProperty(nameof(TestAttributedRoom.Name));
        Assert.False(name.TryGetProperty("attributes", out _));
        Assert.DoesNotContain("Secret spare", property.GetRawText() + browse.GetRawText());
    }

    [Fact]
    public async Task WhenNoPolicyIsSet_ThenASubjectAnAttributeHoldsIsReachedThroughItsOwnPath()
    {
        // Arrange
        var root = CreateRoot();
        var configuration = CreateConfiguration(policy: null);

        // Act
        var search = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json", types = new[] { nameof(TestDevice) } });
        var property = await McpToolCaller.CallAsync(root, configuration, "get_property", new { path = "Items[room].Spare.DeviceName" });

        // Assert
        Assert.Contains("Items[room].Spare", McpToolCaller.GetResultPaths(search));
        Assert.Equal("Secret spare", property.GetProperty("value").GetString());
    }
}
