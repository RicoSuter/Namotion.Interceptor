using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Mcp.Tests.TestGraphs;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class ReadabilityAccessTests
{
    [Fact]
    public async Task WhenNoPolicyIsSetAndTheRootIsNotRegistered_ThenSearchListsNoSubject()
    {
        // Arrange
        // The root's context offers the registry of another context but does not register the root itself.
        var registeredContext = CreateContext();
        _ = new TestDevice(registeredContext) { DeviceName = "Elsewhere" };
        var registry = registeredContext.GetService<ISubjectRegistry>();
        var root = new TestRoom(InterceptorSubjectContext.Create().WithService(() => registry)) { Name = "Unregistered" };
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var result = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json" });

        // Assert
        Assert.Null(root.TryGetRegisteredSubject());
        Assert.Empty(result.GetProperty("results").EnumerateObject());
    }

    [Fact]
    public async Task WhenASubjectIsAtTheEndOfADeepChainOfSingleParents_ThenSearchChecksItWithoutOverflowingTheStack()
    {
        // Arrange
        // The device is the only candidate of its type, so its chain is walked first, from the bottom.
        const int depth = 100_000;
        var context = CreateContext();
        var root = new TestMixedGroup(context);
        var current = root;
        for (var level = 0; level < depth; level++)
        {
            var next = new TestMixedGroup(context);
            current.Items = new Dictionary<string, IInterceptorSubject> { ["x"] = next };
            current = next;
        }

        current.Items = new Dictionary<string, IInterceptorSubject> { ["device"] = new TestDevice(context) { DeviceName = "Deep" } };
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            AccessPolicy = new TestAccessPolicy((_, _) => true)
        };

        // Act
        var result = await McpToolCaller.CallAsync(root, configuration, "search",
            new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.Single(result.GetProperty("results").EnumerateObject());
    }

    [Fact]
    public void WhenNoPolicyIsSet_ThenEveryCallSharesOneReadabilityInWhichEverySubjectIsReadable()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = new TestDevice(context) { DeviceName = "Light" };
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var first = McpSubjectReadability.For(configuration, room);
        var second = McpSubjectReadability.For(configuration, new TestRoom(context));

        // Assert
        Assert.Same(first, second);
        Assert.True(first.IsReadableWithPathCheck(room.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public void WhenAskedForTheReadableSubjectsPathProviderTwice_ThenTheReadabilityReturnsTheSameOne()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            AccessPolicy = new TestAccessPolicy((_, _) => true)
        };
        var readability = McpSubjectReadability.For(configuration, room);

        // Act
        var first = readability.GetReadableSubjectsPathProvider(configuration.ToolPathProvider);
        var again = readability.GetReadableSubjectsPathProvider(configuration.ToolPathProvider);

        // Assert
        Assert.Same(first, again);
    }

    [Fact]
    public async Task WhenAPolicyCallbackMovesASubjectOnThePathBehindAHiddenProperty_ThenTheRestOfThePathDoesNotResolve()
    {
        // Arrange
        // The path passes the root's A and then A's Child. When the policy is asked about A's Child, it moves A behind
        // the hidden Secret, so A is no longer readable when its Child is looked up.
        var context = CreateContext();
        var root = new TestNode(context) { Name = "Root" };
        var a = new TestNode(context) { Name = "A" };
        root.A = a;
        a.Child = new TestNode(context) { Name = "S" };

        var isMoved = false;
        var policy = new TestAccessPolicy((member, action) =>
        {
            if (!isMoved && member.Parent.Subject == a && member.Name == nameof(TestNode.Child))
            {
                isMoved = true;
                root.Secret = a;
                root.A = null;
            }

            return action != McpAccessActions.Read || member.Name != nameof(TestNode.Secret);
        });
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance, AccessPolicy = policy };

        // Act
        var result = await McpToolCaller.CallAsync(root, configuration, "get_property", new { path = "A.Child.Name" });

        // Assert
        Assert.True(isMoved);
        Assert.Equal("Path not found: A.Child.Name", result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenThePolicyDeniesEveryIncludedPropertyOfASubject_ThenItIsHiddenWhateverItAllowsOnExcludedOnes(bool isExcludedPropertyDenied)
    {
        // Arrange
        // The provider excludes the device's IsOn, so only DeviceName decides whether the device is empty.
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };
        var configuration = new McpServerConfiguration
        {
            PathProvider = new ExcludingPathProvider(nameof(TestDevice.IsOn)),
            AccessPolicy = isExcludedPropertyDenied
                ? TestAccessPolicy.DenyRead(nameof(TestDevice.DeviceName), nameof(TestDevice.IsOn))
                : TestAccessPolicy.DenyRead(nameof(TestDevice.DeviceName))
        };

        // Act
        var isReadable = McpSubjectReadability.For(configuration, room).IsReadableWithPathCheck(room.Device.TryGetRegisteredSubject()!);
        var subject = configuration.TryGetReadableSubject(room.TryGetRegisteredSubject()!, nameof(TestRoom.Device));
        var result = await McpToolCaller.CallAsync(room, configuration, "search", new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.False(isReadable);
        Assert.Null(subject);
        Assert.Empty(result.GetProperty("results").EnumerateObject());
    }
}
