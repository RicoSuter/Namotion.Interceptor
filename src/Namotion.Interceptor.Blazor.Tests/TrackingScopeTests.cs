using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;
using Xunit;

namespace Namotion.Interceptor.Blazor.Tests;

public class TrackingScopeTests
{
    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenPropertyReadByADescendantChanges_ThenTheDescendantRendersTheNewValue(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read("reader", person, log)));
        await WaitAsync(() => log.Value("reader") == "Initial");

        // Act
        person.FirstName = "First";
        await WaitAsync(() => log.Value("reader") == "First");
        person.FirstName = "Second";

        // Assert
        await WaitAsync(() => log.Value("reader") == "Second");
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAChildRendersItsContentOnlyFromItsSecondRender_ThenAChangeOfWhatTheContentReadsIsRendered(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(mode);
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read<DeferredReader>("reader", person, log)));

        // The empty first render, the content in a batch of its own and the render of the scope after its first render.
        await WaitAsync(() => log.Renders("reader") >= 3 && renderer.UnacknowledgedBatchCount == 0);

        // Act
        person.FirstName = "Changed";

        // Assert
        await WaitAsync(() => log.Value("reader") == "Changed");
        Assert.Empty(renderer.Exceptions);
    }

    [Fact]
    public async Task WhenTheScopeIsDisposedBeforeItsFirstBatchIsAcknowledged_ThenItStopsRecording()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        // Gives this flow the list of recording scopes, which the render started from it shares.
        ReadPropertyRecorder.Start().Dispose();
        var scopeCountBefore = ReadPropertyRecorder.ActiveScopeCount;

        var renderer = new TestRenderer(TestRenderMode.Server);
        renderer.HoldAcknowledgements();
        await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read("reader", person, log)));
        await WaitAsync(() => log.Value("reader") == "Initial");
        var scopeCountWhileRendered = ReadPropertyRecorder.ActiveScopeCount;

        // Act
        await renderer.EndAsync();
        person.FirstName = "Changed";

        // Assert
        Assert.Equal(scopeCountBefore + 1, scopeCountWhileRendered);
        Assert.Equal(scopeCountBefore, ReadPropertyRecorder.ActiveScopeCount);
        Assert.Empty(renderer.Exceptions);
    }

    [Fact]
    public async Task WhenPrerendered_ThenTheScopeRecordsUntilItIsDisposed()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        ReadPropertyRecorder.Start().Dispose();
        var scopeCountBefore = ReadPropertyRecorder.ActiveScopeCount;

        var renderer = new TestRenderer(TestRenderMode.Prerender);
        var host = await RenderAsync(renderer, Fragments.Scope(context, Fragments.Read("reader", person, log)));
        await WaitAsync(() => log.Value("reader") == "Initial");

        // Act
        await host.RenderAgainAsync();
        var scopeCountWhileRendered = ReadPropertyRecorder.ActiveScopeCount;
        await renderer.EndAsync();

        // Assert
        Assert.Equal(2, log.Renders("reader"));
        Assert.Equal(scopeCountBefore + 1, scopeCountWhileRendered);
        Assert.Equal(scopeCountBefore, ReadPropertyRecorder.ActiveScopeCount);
        Assert.Empty(renderer.Exceptions);
    }

    internal static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();
    }

    internal static Task<Host> RenderAsync(TestRenderer renderer, RenderFragment content)
    {
        return renderer.RenderAsync<Host>(new Dictionary<string, object?> { [nameof(Host.Content)] = content });
    }

    internal static Task WaitAsync(Func<bool> condition)
    {
        return AsyncTestHelpers.WaitUntilAsync(condition, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(5));
    }
}
