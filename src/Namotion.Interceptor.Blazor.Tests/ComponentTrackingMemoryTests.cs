using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Blazor.ComponentTracking;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;
using static Namotion.Interceptor.Blazor.Tests.ComponentTrackingTestHelpers;
using static Namotion.Interceptor.Blazor.Tests.TrackingScopeTests;

namespace Namotion.Interceptor.Blazor.Tests;

/// <summary>
/// A subject outlives the circuits that show it, so it must not keep their subscriptions, components or dispatchers.
/// </summary>
public class ComponentTrackingMemoryTests
{
    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheCircuitEnds_ThenTheSubjectsKeepNoSubscription(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var first = new Person(context) { FirstName = "First" };
        var second = new Person(context) { FirstName = "Second" };
        var log = new RenderLog();
        var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Both(
            Fragments.Read("first", first, log),
            Fragments.Both(Fragments.Read("second", second, log), Fragments.Read("again", first, log))));
        await WaitAsync(() => log.Renders("again") > 0 && SubscriptionCount(first) == 1 && SubscriptionCount(second) == 1);

        // Act
        await renderer.EndAsync();
        await ((IAsyncDisposable)renderer.Services).DisposeAsync();

        // Assert
        Assert.Equal(0, SubscriptionCount(first));
        Assert.Equal(0, SubscriptionCount(second));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheCircuitEnds_ThenItsComponentsAndDispatcherCanBeCollected(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var (component, dispatcher) = await RenderAndEndAsync(mode, person);

        // Act: a continuation of the ended renderer can still be on its way, so collect until it has run.
        await WaitAsync(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return !component.IsAlive && !dispatcher.IsAlive;
        });

        // Assert
        Assert.False(component.IsAlive);
        Assert.False(dispatcher.IsAlive);
        Assert.Equal(0, SubscriptionCount(person));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenComponentsAreReplacedManyTimes_ThenOnlyTheShownPropertiesStaySubscribed(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var log = new RenderLog();
        var shown = new List<Person>();
        var replaced = new List<Person>();
        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, _ => { });
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();

        // Act
        for (var round = 0; round < 100; round++)
        {
            replaced.AddRange(shown);
            shown = Enumerable.Range(0, 5).Select(index => new Person(context) { FirstName = $"{round}-{index}" }).ToList();
            var content = Fragments.Each(shown.Count, index => Fragments.Read($"reader{index}", shown[index], log));
            await renderer.Dispatcher.InvokeAsync(() => host.SetContent(content));
        }

        await SettleAsync(renderer);
        await renderer.Dispatcher.InvokeAsync(dispatcher.Sweep);

        // Assert
        Assert.Equal(shown.Count, dispatcher.SubscriptionCount);
        Assert.All(shown, person => Assert.Equal(1, SubscriptionCount(person)));
        Assert.All(replaced, person => Assert.Equal(0, SubscriptionCount(person)));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenComponentsAreReplacedManyTimesWithoutAnExplicitSweep_ThenTheSubscriptionsStayBounded(TestRenderMode mode)
    {
        // Arrange: a sweep also runs once the trackers have doubled, so removed components cannot pile up for a second.
        var context = CreateContext();
        var log = new RenderLog();
        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, _ => { });
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();

        // Act
        for (var round = 0; round < 100; round++)
        {
            var people = Enumerable.Range(0, 5).Select(index => new Person(context) { FirstName = $"{round}-{index}" }).ToList();
            var content = Fragments.Each(people.Count, index => Fragments.Read($"reader{index}", people[index], log));
            await renderer.Dispatcher.InvokeAsync(() => host.SetContent(content));
        }

        // Assert: 500 components read 500 properties in total; without the sweeps all of them would stay subscribed.
        Assert.InRange(dispatcher.SubscriptionCount, 5, 150);
    }

    // Kept out of the test method, so that no local of the test keeps the component or the dispatcher alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Component, WeakReference Dispatcher)> RenderAndEndAsync(TestRenderMode mode, Person person)
    {
        var log = new RenderLog();
        var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await WaitAsync(() => log.Renders("reader") > 0 && SubscriptionCount(person) == 1);
        var references = (new WeakReference(host), new WeakReference(renderer.Services.GetRequiredService<ComponentTrackingDispatcher>()));

        await renderer.EndAsync();
        await ((IAsyncDisposable)renderer.Services).DisposeAsync();
        return references;
    }

    private static int SubscriptionCount(Person person) =>
        ((IInterceptorSubject)person).Data.TryGetValue((nameof(Person.FirstName), PropertyChangeSubscription.ListenersKey), out var subscriptions) &&
        subscriptions is Array array ? array.Length : 0;
}
