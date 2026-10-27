using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

public class VisibilityAccessTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    [Fact]
    public async Task WhenNoPolicyIsSetAndTheRootIsNotRegistered_ThenSearchAnswersAsBefore()
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
    public void WhenNoPolicyIsSet_ThenEveryCallSharesOneVisibilityThatShowsEverySubject()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = new TestDevice(context) { DeviceName = "Light" };
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var first = McpVisibility.For(configuration, room);
        var second = McpVisibility.For(configuration, new TestRoom(context));

        // Assert
        Assert.Same(first, second);
        Assert.True(first.IsShown(room.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public void WhenAskedForAnotherInnerProvider_ThenTheVisibilityWrapsThatProvider()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            AccessPolicy = new TestAccessPolicy((_, _) => true)
        };
        var visibility = McpVisibility.For(configuration, room);

        // Act
        var first = visibility.GetListingPathProvider(DefaultPathProvider.Instance);
        var again = visibility.GetListingPathProvider(DefaultPathProvider.Instance);
        var other = visibility.GetListingPathProvider(new SlashPathProvider());

        // Assert
        Assert.Same(first, again);
        Assert.Equal('.', first.PathSeparator);
        Assert.Equal('/', other.PathSeparator);
    }

    private sealed class SlashPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';
    }
}
