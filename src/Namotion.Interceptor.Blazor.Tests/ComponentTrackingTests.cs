using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Blazor.ComponentTracking;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;
using Xunit.Abstractions;
using static Namotion.Interceptor.Blazor.Tests.ComponentTrackingTestHelpers;
using static Namotion.Interceptor.Blazor.Tests.TrackingScopeTests;

namespace Namotion.Interceptor.Blazor.Tests;

/// <summary>
/// Per-component tracking: each change renders exactly the components whose render read the changed property, once,
/// wherever and whenever they render. Each test writes its counts to the test output before it asserts them.
/// </summary>
public class ComponentTrackingTests
{
    private const int SiblingCount = 20;

    private readonly ITestOutputHelper _output;

    public ComponentTrackingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenOneOfManySiblingsReadsTheChangedProperty_ThenOnlyItRendersOnceInOneBatch(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var people = Enumerable.Range(0, SiblingCount).Select(index => new Person(context) { FirstName = $"Initial {index}" }).ToArray();
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Each(SiblingCount, index => Fragments.Read($"reader{index}", people[index], log)));
        await SettleAsync(renderer);
        var rendersBefore = Enumerable.Range(0, SiblingCount).Select(index => log.Renders($"reader{index}")).ToArray();
        var batchesBefore = renderer.BatchCount;

        // Act
        people[7].FirstName = "Changed";
        await WaitAsync(() => log.Value("reader7") == "Changed");
        await SettleAsync(renderer);

        // Assert
        var renders = Enumerable.Range(0, SiblingCount).Select(index => log.Renders($"reader{index}") - rendersBefore[index]).ToArray();
        _output.WriteLine($"{mode}: renders per sibling after one change: {string.Join(",", renders)}; batches: {renderer.BatchCount - batchesBefore}");
        Assert.Equal(1, renders[7]);
        Assert.Equal(1, renders.Sum());
        Assert.Equal(1, renderer.BatchCount - batchesBefore);
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenManySiblingsReadChangedProperties_ThenEachRendersOnceAndEachDrainInOneBatch(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var people = Enumerable.Range(0, SiblingCount).Select(index => new Person(context) { FirstName = $"Initial {index}" }).ToArray();
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode, throttleMilliseconds: 200);
        await RenderAsync(renderer, Fragments.Each(SiblingCount, index => Fragments.Read($"reader{index}", people[index], log)));
        await SettleAsync(renderer);
        var rendersBefore = Enumerable.Range(0, SiblingCount).Select(index => log.Renders($"reader{index}")).ToArray();
        var batchesBefore = renderer.BatchCount;
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();
        var drainsBefore = dispatcher.DrainCount;

        // Act
        foreach (var person in people)
        {
            person.FirstName = "Changed";
        }

        await WaitAsync(() => Enumerable.Range(0, SiblingCount).All(index => log.Value($"reader{index}") == "Changed"));
        await SettleAsync(renderer);

        // Assert
        var renders = Enumerable.Range(0, SiblingCount).Select(index => log.Renders($"reader{index}") - rendersBefore[index]).ToArray();
        var drains = dispatcher.DrainCount - drainsBefore;
        _output.WriteLine($"{mode}: renders per sibling after a change of each: {string.Join(",", renders)}; drains: {drains}, batches: {renderer.BatchCount - batchesBefore}");
        Assert.All(renders, count => Assert.Equal(1, count));
        Assert.Equal(drains, renderer.BatchCount - batchesBefore);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsPassedToAChildComponent_ThenTheChildRendersAndNotItsParent(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var other = new Person(context) { FirstName = "Other" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Both(
            Fragments.Inline("parent", other, log),
            Box.Of(Fragments.Inline("content", person, log))));
        await SettleAsync(renderer);
        var parentBefore = log.Renders("parent");
        var contentBefore = log.Renders("content");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("content") == "Changed");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: content renders: {log.Renders("content") - contentBefore}, parent renders: {log.Renders("parent") - parentBefore}");
        Assert.Equal(1, log.Renders("content") - contentBefore);
        Assert.Equal(0, log.Renders("parent") - parentBefore);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsInsideACascadingValue_ThenItUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Box.Of(Fragments.Cascading(Fragments.Inline("cascaded", person, log))));
        await SettleAsync(renderer);
        var before = log.Renders("cascaded");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("cascaded") == "Changed");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: cascaded content renders: {log.Renders("cascaded") - before}");
        Assert.Equal(1, log.Renders("cascaded") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAParentAndItsChildReadTheSameProperty_ThenEachRendersExactlyOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Box.Of(Fragments.Both(
            Fragments.Inline("parent", person, log),
            Fragments.Read("child", person, log))));
        await SettleAsync(renderer);
        var parentBefore = log.Renders("parent");
        var childBefore = log.Renders("child");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("parent") == "Changed" && log.Value("child") == "Changed");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: parent renders: {log.Renders("parent") - parentBefore}, child renders: {log.Renders("child") - childBefore}");
        Assert.Equal(1, log.Renders("parent") - parentBefore);
        Assert.Equal(1, log.Renders("child") - childBefore);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAComponentRendersLaterInABatchOfItsOwn_ThenItUpdates(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Read<DeferredReader>("deferred", person, log));
        await WaitAsync(() => log.Value("deferred") == "Initial");
        await SettleAsync(renderer);
        var before = log.Renders("deferred");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("deferred") == "Changed");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: deferred renders: {log.Renders("deferred") - before}");
        Assert.Equal(1, log.Renders("deferred") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenLazyContentOpensAfterAChange_ThenItShowsAndKeepsTheCurrentValue(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, LazyPanel.Of(Fragments.Read("lazy", person, log)));
        await SettleAsync(renderer);
        person.FirstName = "While closed";

        // Act
        await renderer.Dispatcher.InvokeAsync(() => LazyPanel.Instances.Single().Open());
        await WaitAsync(() => log.Value("lazy") == "While closed");
        person.FirstName = "While open";
        await WaitAsync(() => log.Value("lazy") == "While open");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: lazy renders: {log.Renders("lazy")}");
        Assert.Equal(2, log.Renders("lazy"));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenWritersOnManyThreadsChangeAProperty_ThenTheLastRenderShowsTheLastValue(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode, throttleMilliseconds: 5);
        await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        await Task.WhenAll(Enumerable.Range(0, 4).Select(thread => Task.Run(() =>
        {
            for (var index = 0; index < 2_000; index++)
            {
                person.FirstName = $"{thread}-{index}";
            }
        })));
        person.FirstName = "Last";
        await WaitAsync(() => log.Value("reader") == "Last");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: 8,001 changes rendered {log.Renders("reader") - before} times");
        Assert.Equal("Last", log.Value("reader"));
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheClientHoldsAcknowledgements_ThenTheLastRenderShowsTheLastValue(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode, maximumUnacknowledgedBatches: 2);
        await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await SettleAsync(renderer);

        // Act
        renderer.HoldAcknowledgements();
        for (var index = 0; index < 10; index++)
        {
            person.FirstName = $"Value {index}";
            await WaitAsync(() => renderer.Services.GetRequiredService<ComponentTrackingDispatcher>().IsIdle);
        }

        renderer.ReleaseAcknowledgements();
        await WaitAsync(() => log.Value("reader") == "Value 9");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: deferred render requests: {renderer.DeferredRenderRequests}");
        Assert.Equal("Value 9", log.Value("reader"));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAComponentRefusesToRenderAgain_ThenATrackedChangeStillRendersIt(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Read<RenderOnceReader>("reader", person, log));
        await SettleAsync(renderer);

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("reader") == "Changed");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(2, log.Renders("reader"));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAPropertyChangesManyTimesWithinTheThrottleInterval_ThenTheReaderRendersAtMostTwice(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode, throttleMilliseconds: 1_000);
        await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        for (var index = 0; index < 100; index++)
        {
            person.FirstName = $"Value {index}";
        }

        await WaitAsync(() => log.Value("reader") == "Value 99");
        await SettleAsync(renderer);

        // Assert
        var renders = log.Renders("reader") - before;
        _output.WriteLine($"{mode}: 100 changes within the throttle interval rendered {renders} times");
        Assert.InRange(renders, 1, 2);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsInsideNestedCascadingValues_ThenItUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Cascading(Fragments.Cascading(Fragments.Inline("cascaded", person, log))));
        await SettleAsync(renderer);
        var before = log.Renders("cascaded");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("cascaded") == "Changed");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("cascaded") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsInsideATrackingScope_ThenItUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Both(
            Fragments.Inline("inline", person, log),
            Fragments.Read("reader", person, log))));
        await WaitAsync(() => log.Renders("reader") >= 2);
        await SettleAsync(renderer);
        var inlineBefore = log.Renders("inline");
        var readerBefore = log.Renders("reader");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("inline") == "Changed" && log.Value("reader") == "Changed");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: inline renders: {log.Renders("inline") - inlineBefore}, reader renders: {log.Renders("reader") - readerBefore}");
        Assert.Equal(1, log.Renders("inline") - inlineBefore);
        Assert.Equal(1, log.Renders("reader") - readerBefore);
    }

    [Fact]
    public void WhenTheFrameworkIsReferenced_ThenThePrivateMembersAreFound()
    {
        // Act & Assert
        Assert.True(ComponentBaseAccessors.IsSupported);
    }

    [Fact]
    public async Task WhenPrerendering_ThenNothingIsSubscribed()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(TestRenderMode.Prerender);

        // Act
        await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await WaitAsync(() => log.Renders("reader") == 1);

        // Assert
        Assert.Equal(0, renderer.Services.GetRequiredService<ComponentTrackingDispatcher>().SubscriptionCount);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAComponentIsRemoved_ThenItsSubscriptionsAreReleased(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await SettleAsync(renderer);
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();
        var subscribed = dispatcher.SubscriptionCount;

        // Act
        await renderer.Dispatcher.InvokeAsync(() => host.SetContent(_ => { }));
        await SettleAsync(renderer);
        await renderer.Dispatcher.InvokeAsync(dispatcher.Sweep);

        // Assert
        _output.WriteLine($"{mode}: subscriptions before removal: {subscribed}, after: {dispatcher.SubscriptionCount}");
        Assert.Equal(1, subscribed);
        Assert.Equal(0, dispatcher.SubscriptionCount);
    }

    private static TestRenderer CreateRenderer(TestRenderMode mode, int throttleMilliseconds = 0, int maximumUnacknowledgedBatches = 10)
    {
        return new TestRenderer(mode, maximumUnacknowledgedBatches, services => services
            .AddComponentTracking(options => options.ThrottleMilliseconds = throttleMilliseconds));
    }
}

/// <summary>Renders only once on its own, as a component that renders again only when its data changes does.</summary>
public sealed class RenderOnceReader : Reader
{
    protected override bool ShouldRender() => false;
}

/// <summary>Renders its content, as a layout or card component does.</summary>
public sealed class Box : ComponentBase
{
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    public static RenderFragment Of(RenderFragment content) => builder =>
    {
        builder.OpenComponent<Box>(0);
        builder.AddComponentParameter(1, nameof(ChildContent), content);
        builder.CloseComponent();
    };

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
}

/// <summary>Renders its content only once opened, as a tab panel or an expansion panel does.</summary>
public sealed class LazyPanel : ComponentBase
{
    public static readonly List<LazyPanel> Instances = [];

    private bool _isOpen;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    public static RenderFragment Of(RenderFragment content) => builder =>
    {
        builder.OpenComponent<LazyPanel>(0);
        builder.AddComponentParameter(1, nameof(ChildContent), content);
        builder.CloseComponent();
    };

    public void Open()
    {
        _isOpen = true;
        StateHasChanged();
    }

    protected override void OnInitialized()
    {
        Instances.Clear();
        Instances.Add(this);
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_isOpen)
        {
            builder.AddContent(0, ChildContent);
        }
    }
}
