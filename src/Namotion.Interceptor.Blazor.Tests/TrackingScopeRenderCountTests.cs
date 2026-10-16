using System.Diagnostics;
using Namotion.Interceptor.Testing;
using Xunit;
using Xunit.Abstractions;
using static Namotion.Interceptor.Blazor.Tests.TrackingScopeTests;

namespace Namotion.Interceptor.Blazor.Tests;

/// <summary>
/// Counts the renders that changes cause: at least one after every change that a scope read, and no more than the
/// documented ones. Each test writes its counts to the test output before it asserts them.
/// </summary>
public class TrackingScopeRenderCountTests
{
    private const int SiblingCount = 20;

    private static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public TrackingScopeRenderCountTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenOneScopeTracksAProperty_ThenEachChangeRendersOnceAndAnUnrelatedChangeNever(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var unrelated = new Person(context) { FirstName = "Unrelated" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read("reader", person, log)));
        await SettleAsync(renderer, () => log.Renders("reader") >= 2);
        var initialRenders = log.Renders("reader");

        // Act
        var staleChanges = 0;
        for (var index = 0; index < 5; index++)
        {
            var expected = $"Value {index}";
            unrelated.FirstName = $"Unrelated {index}";
            person.FirstName = expected;
            staleChanges += await IsStaleAsync(renderer, () => log.Value("reader") == expected);
        }

        // Assert
        var changeRenders = log.Renders("reader") - initialRenders;
        _output.WriteLine($"{mode}: first render and the render after it: {initialRenders}; 5 changes, each after an unrelated one: {changeRenders} renders, {staleChanges} never rendered");
        Assert.Equal(0, staleChanges);
        Assert.Equal(2, initialRenders);
        Assert.Equal(5, changeRenders);
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenScopesAreNested_ThenAChangeInsideTheInnerScopeAlsoRendersTheOuterScope(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var outerPerson = new Person(context) { FirstName = "Outer" };
        var innerPerson = new Person(context) { FirstName = "Inner" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Both(
            Fragments.Read("outer", outerPerson, log),
            Fragments.Scope(context, Fragments.Read("inner", innerPerson, log)))));

        // The render after the first render of the outer scope renders the inner scope again, and the inner scope then
        // renders once more on its own, unless it already waits for a render when the outer one renders.
        await SettleAsync(renderer, () => log.Renders("outer") >= 2 && log.Renders("inner") >= 3);
        var (outerInitial, innerInitial) = (log.Renders("outer"), log.Renders("inner"));

        // Act
        outerPerson.FirstName = "Outer changed";
        var outerStale = await IsStaleAsync(renderer, () => log.Value("outer") == "Outer changed" && log.Renders("inner") > innerInitial);
        var (outerAfterOuter, innerAfterOuter) = (log.Renders("outer"), log.Renders("inner"));

        // Both scopes render for a change read inside the inner scope. On WebAssembly the inner one renders for the
        // render of the outer one as well, and on its own; on a server both requests can render in one batch.
        var expectedInnerRenders = mode == TestRenderMode.WebAssembly ? 2 : 1;
        innerPerson.FirstName = "Inner changed";
        var innerStale = await IsStaleAsync(renderer, () =>
            log.Value("inner") == "Inner changed" &&
            log.Renders("outer") > outerAfterOuter &&
            log.Renders("inner") >= innerAfterOuter + expectedInnerRenders);
        var (outerAfterInner, innerAfterInner) = (log.Renders("outer"), log.Renders("inner"));

        // Assert
        _output.WriteLine($"{mode}: first renders: outer {outerInitial}, inner {innerInitial}; " +
            $"change read by the outer scope only: outer +{outerAfterOuter - outerInitial}, inner +{innerAfterOuter - innerInitial}; " +
            $"change read inside the inner scope: outer +{outerAfterInner - outerAfterOuter}, inner +{innerAfterInner - innerAfterOuter}; " +
            $"never rendered: {innerStale + outerStale}");
        Assert.Equal(0, innerStale + outerStale);
        Assert.Equal(2, outerInitial);
        Assert.InRange(innerInitial, 2, 3);
        Assert.Equal(1, outerAfterOuter - outerInitial);
        Assert.Equal(1, innerAfterOuter - innerInitial);
        Assert.Equal(1, outerAfterInner - outerAfterOuter);
        Assert.InRange(innerAfterInner - innerAfterOuter, expectedInnerRenders, 2);
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenSiblingScopesRenderInOneBatch_ThenEarlierSiblingsAlsoTrackTheLaterOnesUntilTheyRenderOnTheirOwn(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var people = Enumerable.Range(0, SiblingCount).Select(index => new Person(context) { FirstName = $"Person {index}" }).ToArray();
        var keys = Enumerable.Range(0, SiblingCount).Select(index => $"reader {index}").ToArray();
        var log = new RenderLog();
        var last = SiblingCount - 1;

        // Every sibling reads the barrier as well. Once every sibling rendered for a change of the barrier, every sibling
        // has handled the changes before it, because a scope handles its windows of changes in order.
        var barrier = new Person(context) { FirstName = "Barrier" };

        await using var renderer = new TestRenderer(mode);
        var host = await RenderAsync(renderer, Fragments.Each(SiblingCount,
            index => Fragments.Scope(context, Fragments.Read(keys[index], people[index], log, barrier))));
        await SettleAsync(renderer, () => keys.All(key => log.Renders(key) >= 2));
        var initialRenders = keys.Sum(log.Renders);

        // Act
        var afterFirstRenders = await ChangeLastAsync("after the first renders", null);
        await PassBarrierAsync("before the render of the parent");

        // The parent renders every scope in one batch, so each scope also records the reads of the siblings after it.
        var beforeParentRender = keys.Select(log.Renders).ToArray();
        await host.RenderAgainAsync();
        await WaitAsync(() => keys.Select(log.Renders).Zip(beforeParentRender).All(pair => pair.First > pair.Second) && renderer.UnacknowledgedBatchCount == 0);
        var afterParentRender = await ChangeLastAsync("after the render of the parent", SiblingCount - 1);
        await PassBarrierAsync("after the render of the parent");
        var afterOwnRenders = await ChangeLastAsync("after the own renders", null);

        // Assert
        _output.WriteLine($"{mode}: {SiblingCount} scopes rendered {initialRenders} times for their first render and the render after it");
        _output.WriteLine($"{mode}: siblings that rendered for a change of the last sibling: {Describe(afterFirstRenders)} after the first renders, " +
            $"{Describe(afterParentRender)} after a render of the parent, {Describe(afterOwnRenders)} after the renders of a change that all read");
        Assert.Equal(2 * SiblingCount, initialRenders);
        Assert.Equal(SiblingCount - 1, afterParentRender.Others);
        if (mode == TestRenderMode.WebAssembly)
        {
            // Each scope renders in a batch of its own after its first render and after a change, and then tracks only its own reads.
            Assert.All([afterFirstRenders, afterParentRender, afterOwnRenders], result => Assert.Equal(1, result.Last));
            Assert.Equal(0, afterFirstRenders.Others);
            Assert.Equal(0, afterOwnRenders.Others);
        }
        else
        {
            // Render requests that wait for an acknowledgement render together in one batch, in the order of the requests.
            Assert.All([afterFirstRenders, afterParentRender, afterOwnRenders], result => Assert.InRange(result.Last, 1, 2));
            Assert.InRange(afterFirstRenders.Others, 0, SiblingCount - 1);
            Assert.InRange(afterOwnRenders.Others, 0, SiblingCount - 1);
        }

        Assert.Empty(renderer.Exceptions);

        // Changes the last person and returns how often its sibling rendered and how many other siblings rendered.
        async Task<(int Last, int Others)> ChangeLastAsync(string value, int? expectedOthers)
        {
            var before = keys.Select(log.Renders).ToArray();

            // The first sibling renders once more for the change of the first person below.
            int Others(int firstRenders) => Enumerable.Range(0, last).Count(index => log.Renders(keys[index]) > before[index] + (index == 0 ? firstRenders : 0));

            people[last].FirstName = value;
            await WaitAsync(() => log.Value(keys[last]) == value && renderer.UnacknowledgedBatchCount == 0);
            if (expectedOthers is not null)
            {
                await WaitAsync(() => Others(0) >= expectedOthers && renderer.UnacknowledgedBatchCount == 0);
            }

            // A change of the first person, read by the first sibling only, makes most windows of changes of the other
            // siblings end before its render. The windows are not aligned, so this finds most renders, not all.
            people[0].FirstName = value;
            await WaitAsync(() => log.Value(keys[0]) == value && renderer.UnacknowledgedBatchCount == 0);

            return (log.Renders(keys[last]) - before[last], Others(1));
        }

        async Task PassBarrierAsync(string value)
        {
            var before = keys.Select(log.Renders).ToArray();
            barrier.FirstName = value;
            await WaitAsync(() => keys.Select(log.Renders).Zip(before).All(pair => pair.First > pair.Second) && renderer.UnacknowledgedBatchCount == 0);
        }

        static string Describe((int Last, int Others) result) => $"the last one {result.Last} times and {result.Others} others";
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAChildRendersItsContentOnlyFromItsSecondRender_ThenEachChangeRendersItOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read<DeferredReader>("reader", person, log)));
        await SettleAsync(renderer, () => log.Renders("reader") >= 3);
        var initialRenders = log.Renders("reader");

        // Act
        var staleChanges = 0;
        for (var index = 0; index < 3; index++)
        {
            var expected = $"Value {index}";
            person.FirstName = expected;
            staleChanges += await IsStaleAsync(renderer, () => log.Value("reader") == expected);
        }

        // Assert
        var changeRenders = log.Renders("reader") - initialRenders;
        _output.WriteLine($"{mode}: first renders: {initialRenders}; 3 changes: {changeRenders} renders, {staleChanges} never rendered");
        Assert.Equal(0, staleChanges);
        Assert.Equal(3, initialRenders);
        Assert.Equal(3, changeRenders);
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAPropertyChangesInABurst_ThenTheRendersAreBoundedAndTheLastValueIsRendered(TestRenderMode mode)
    {
        // Arrange
        const int changeCount = 100;
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read("reader", person, log)));
        await SettleAsync(renderer, () => log.Renders("reader") >= 2);
        var initialRenders = log.Renders("reader");
        var stopwatch = Stopwatch.StartNew();

        // Act
        for (var index = 1; index <= changeCount; index++)
        {
            person.FirstName = $"Value {index}";
        }

        var stale = await IsStaleAsync(renderer, () => log.Value("reader") == $"Value {changeCount}");
        stopwatch.Stop();

        // Assert
        var renders = log.Renders("reader") - initialRenders;

        // A scope renders at most once per window of changes, and once more for the window open when the burst ended.
        var windows = (int)(stopwatch.ElapsedMilliseconds / 33) + 2;
        _output.WriteLine($"{mode}: {changeCount} changes, last value rendered after {stopwatch.ElapsedMilliseconds} ms: {renders} renders, last value never rendered: {stale == 1}");
        Assert.Equal(0, stale);
        Assert.InRange(renders, 1, windows);
        Assert.Empty(renderer.Exceptions);
    }

    // Waits until the renders that the first render causes have happened, or reports what happened after a while.
    private static async Task SettleAsync(TestRenderer renderer, Func<bool> condition)
    {
        await WaitAsync(() => renderer.UnacknowledgedBatchCount == 0);
        await TryWaitAsync(renderer, condition, StaleTimeout);
    }

    private static async Task<int> IsStaleAsync(TestRenderer renderer, Func<bool> condition)
    {
        return await TryWaitAsync(renderer, condition, StaleTimeout) ? 0 : 1;
    }

    private static async Task<bool> TryWaitAsync(TestRenderer renderer, Func<bool> condition, TimeSpan timeout)
    {
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => condition() && renderer.UnacknowledgedBatchCount == 0, timeout, TimeSpan.FromMilliseconds(5));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
