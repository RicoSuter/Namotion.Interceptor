using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor.Blazor.ComponentTracking;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;
using static Namotion.Interceptor.Blazor.Tests.ComponentTrackingTestHelpers;
using static Namotion.Interceptor.Blazor.Tests.TrackingScopeTests;

namespace Namotion.Interceptor.Blazor.Tests;

/// <summary>
/// A <see cref="TrackedComponentBase"/> ignores a render of its parent with unchanged parameters, and still renders
/// for a changed parameter, a changed property that its render read, and content passed to it.
/// </summary>
public class TrackedComponentBaseTests
{
    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheParentRendersWithUnchangedParameters_ThenTheComponentDoesNotRender(TestRenderMode mode)
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();
        object? value = new Label("Label", 1);

        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, TrackedReader.Of("reader", person, log, () => value));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        value = new Label("Label", 1);
        await host.RenderAgainAsync();
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(0, log.Renders("reader") - before);
        Assert.Equal(1, log.Renders(TrackedReader.ParametersSetKey("reader")));
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheComponentCallsStateHasChanged_ThenItRendersWithUnchangedParameters(TestRenderMode mode)
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();
        TrackedReader? reader = null;

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, builder =>
        {
            builder.OpenComponent<TrackedReader>(0);
            builder.AddComponentParameter(1, nameof(TrackedReader.Key), "reader");
            builder.AddComponentParameter(2, nameof(TrackedReader.Person), person);
            builder.AddComponentParameter(3, nameof(TrackedReader.Log), log);
            builder.AddComponentReferenceCapture(4, component => reader = (TrackedReader)component);
            builder.CloseComponent();
        });
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        Assert.NotNull(reader);
        await reader.RenderAgainAsync();
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheParentRendersWithAChangedParameter_ThenTheComponentRendersIt(TestRenderMode mode)
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();
        object? value = new Label("Label", 1);

        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, TrackedReader.Of("reader", person, log, () => value));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        value = new Label("Label", 2);
        await host.RenderAgainAsync();
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
        Assert.Equal("Initial Label { Text = Label, Number = 2 }", log.Value("reader"));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAParameterIsRemoved_ThenTheComponentRenders(TestRenderMode mode)
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();
        var hasValue = true;

        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, builder =>
        {
            builder.OpenComponent<TrackedReader>(0);
            builder.AddComponentParameter(1, nameof(TrackedReader.Key), "reader");
            builder.AddComponentParameter(2, nameof(TrackedReader.Person), person);
            builder.AddComponentParameter(3, nameof(TrackedReader.Log), log);
            if (hasValue)
            {
                builder.AddComponentParameter(4, nameof(TrackedReader.Value), "Value");
            }

            builder.CloseComponent();
        });
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        hasValue = false;
        await host.RenderAgainAsync();
        await SettleAsync(renderer);

        // Assert
        // A removed parameter keeps its value, as with every component, but the component renders.
        Assert.Equal(1, log.Renders("reader") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAPropertyItsRenderReadChanges_ThenTheComponentRendersWithUnchangedParameters(TestRenderMode mode)
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, TrackedReader.Of("reader", person, log, () => "Value"));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("reader") == "Changed Value");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsPassedToTheComponent_ThenItRendersWithItsParent(TestRenderMode mode)
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();
        var text = "Before";

        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, TrackedReader.Of("reader", person, log, () => "Value", content => content.AddContent(0, text)));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        text = "After";
        await host.RenderAgainAsync();
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
    }

    [Fact]
    public async Task WhenComponentTrackingIsNotRegistered_ThenTheComponentRendersWithItsParent()
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(TestRenderMode.WebAssembly);
        var host = await RenderAsync(renderer, TrackedReader.Of("reader", person, log, () => "Value"));
        var before = log.Renders("reader");

        // Act
        await host.RenderAgainAsync();

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
    }

    [Fact]
    public async Task WhenPrerendering_ThenTheComponentRendersWithItsParent()
    {
        // Arrange
        var person = new Person(CreateContext()) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(TestRenderMode.Prerender);
        var host = await RenderAsync(renderer, TrackedReader.Of("reader", person, log, () => "Value"));
        var before = log.Renders("reader");

        // Act
        await host.RenderAgainAsync();

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
    }

    private sealed record Label(string Text, int Number);
}

/// <summary>Renders the first name of <see cref="Person"/>, <see cref="Value"/> and its content.</summary>
public sealed class TrackedReader : TrackedComponentBase
{
    [Parameter]
    public string Key { get; set; } = null!;

    [Parameter]
    public Person Person { get; set; } = null!;

    [Parameter]
    public RenderLog Log { get; set; } = null!;

    [Parameter]
    public object? Value { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    public static RenderFragment Of(string key, Person person, RenderLog log, Func<object?> value, RenderFragment? content = null) => builder =>
    {
        builder.OpenComponent<TrackedReader>(0);
        builder.AddComponentParameter(1, nameof(Key), key);
        builder.AddComponentParameter(2, nameof(Person), person);
        builder.AddComponentParameter(3, nameof(Log), log);
        builder.AddComponentParameter(4, nameof(Value), value());
        if (content is not null)
        {
            builder.AddComponentParameter(5, nameof(ChildContent), content);
        }

        builder.CloseComponent();
    };

    public static string ParametersSetKey(string key) => key + ":parameters";

    public Task RenderAgainAsync() => InvokeAsync(StateHasChanged);

    protected override void OnParametersSet() => Log.Record(ParametersSetKey(Key), "");

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var value = $"{Person.FirstName} {Value}";
        builder.AddContent(0, value);
        builder.AddContent(1, ChildContent);
        Log.Record(Key, value);
    }
}
