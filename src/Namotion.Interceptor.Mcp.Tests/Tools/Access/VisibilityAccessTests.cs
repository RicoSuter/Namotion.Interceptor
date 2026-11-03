using System.Diagnostics.CodeAnalysis;
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
    public void WhenNoPolicyIsSet_ThenEveryCallSharesOneVisibilityInWhichEverySubjectIsVisible()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        room.Device = new TestDevice(context) { DeviceName = "Light" };
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var first = McpSubjectVisibility.For(configuration, room);
        var second = McpSubjectVisibility.For(configuration, new TestRoom(context));

        // Assert
        Assert.Same(first, second);
        Assert.True(first.IsVisibleWithPathCheck(room.Device.TryGetRegisteredSubject()!));
    }

    [Fact]
    public void WhenAskedForTheVisibleSubjectsPathProviderTwice_ThenTheVisibilityReturnsTheSameOne()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen" };
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            AccessPolicy = new TestAccessPolicy((_, _) => true)
        };
        var visibility = McpSubjectVisibility.For(configuration, room);

        // Act
        var first = visibility.GetVisibleSubjectsPathProvider(configuration.ToolPathProvider);
        var again = visibility.GetVisibleSubjectsPathProvider(configuration.ToolPathProvider);

        // Assert
        Assert.Same(first, again);
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
        var isVisible = McpSubjectVisibility.For(configuration, room).IsVisibleWithPathCheck(room.Device.TryGetRegisteredSubject()!);
        var subject = configuration.TryGetReadableSubject(room.TryGetRegisteredSubject()!, nameof(TestRoom.Device));
        var result = await McpToolCaller.CallAsync(room, configuration, "search", new { format = "json", types = new[] { nameof(TestDevice) } });

        // Assert
        Assert.False(isVisible);
        Assert.Null(subject);
        Assert.Empty(result.GetProperty("results").EnumerateObject());
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
