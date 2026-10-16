using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Namotion.Interceptor.Blazor.ComponentTracking;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.Blazor.Tests;

/// <summary>
/// Component tracking with the ways Blazor renders content: templates, the components of the framework that render
/// content of others, virtualization, keys, errors, asynchronous initialization and components that change what they read.
/// </summary>
public class ComponentTrackingEdgeCaseTests
{
    private readonly ITestOutputHelper _output;

    public ComponentTrackingEdgeCaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAValueIsReadInATemplate_ThenTheComponentThatRendersTheTemplateRendersOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var people = Enumerable.Range(0, 5).Select(index => new Person(context) { FirstName = $"Initial {index}" }).ToArray();
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, TemplatedList.Of(people, log, person => Fragments.Inline($"item{Array.IndexOf(people, person)}", person, log)));
        await SettleAsync(renderer);
        var listBefore = log.Renders("list");

        // Act
        people[3].FirstName = "Changed";
        await WaitAsync(() => log.Value("item3") == "Changed");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("list") - listBefore);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheSameContentIsRenderedByTwoComponents_ThenEachRendersOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();
        var content = Fragments.Inline("shared", person, log);

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Both(Box.Of(content), Box.Of(content)));
        await SettleAsync(renderer);
        var before = log.Renders("shared");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("shared") == "Changed");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(2, log.Renders("shared") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsPassedThroughADynamicComponent_ThenItUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, builder =>
        {
            builder.OpenComponent<DynamicComponent>(0);
            builder.AddComponentParameter(1, nameof(DynamicComponent.Type), typeof(Box));
            builder.AddComponentParameter(2, nameof(DynamicComponent.Parameters), new Dictionary<string, object>
            {
                [nameof(Box.ChildContent)] = Fragments.Inline("dynamic", person, log)
            });
            builder.CloseComponent();
        });
        await SettleAsync(renderer);
        var before = log.Renders("dynamic");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("dynamic") == "Changed");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("dynamic") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenSectionContentIsRenderedByAnOutletElsewhere_ThenItUpdates(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Both(
            builder =>
            {
                builder.OpenComponent<SectionOutlet>(0);
                builder.AddComponentParameter(1, nameof(SectionOutlet.SectionName), "head");
                builder.CloseComponent();
            },
            Box.Of(builder =>
            {
                builder.OpenComponent<SectionContent>(0);
                builder.AddComponentParameter(1, nameof(SectionContent.SectionName), "head");
                builder.AddComponentParameter(2, nameof(SectionContent.ChildContent), Fragments.Inline("section", person, log));
                builder.CloseComponent();
            })));
        await WaitAsync(() => log.Value("section") == "Initial");
        await SettleAsync(renderer);

        // Act
        person.FirstName = "Changed";

        // Assert
        await WaitAsync(() => log.Value("section") == "Changed");
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenContentIsInsideAnErrorBoundary_ThenItUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, builder =>
        {
            builder.OpenComponent<ErrorBoundary>(0);
            builder.AddComponentParameter(1, nameof(ErrorBoundary.ChildContent), Fragments.Inline("guarded", person, log));
            builder.CloseComponent();
        });
        await SettleAsync(renderer);
        var before = log.Renders("guarded");

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("guarded") == "Changed");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("guarded") - before);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenItemsAreVirtualized_ThenAVisibleItemUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var people = Enumerable.Range(0, 100).Select(index => new Person(context) { FirstName = $"Initial {index}" }).ToArray();
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, builder =>
        {
            builder.OpenComponent<Virtualize<Person>>(0);
            builder.AddComponentParameter(1, nameof(Virtualize<Person>.Items), people);
            builder.AddComponentParameter(2, nameof(Virtualize<Person>.ItemContent),
                (RenderFragment<Person>)(person => Fragments.Inline($"row{Array.IndexOf(people, person)}", person, log)));
            builder.CloseComponent();
        });
        await WaitAsync(() => log.Value("row0") == "Initial 0");
        await SettleAsync(renderer);
        var before = log.Renders("row0");

        // Act
        people[0].FirstName = "Changed";
        await WaitAsync(() => log.Value("row0") == "Changed");
        await SettleAsync(renderer);

        // Assert
        _output.WriteLine($"{mode}: rows rendered at first: {Enumerable.Range(0, 100).Count(index => log.Renders($"row{index}") > 0)}");
        Assert.Equal(1, log.Renders("row0") - before);
        Assert.Equal(0, log.Renders("row99"));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenKeyedReadersAreReordered_ThenEachStillUpdatesOnce(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var people = Enumerable.Range(0, 5).Select(index => new Person(context) { FirstName = $"Initial {index}" }).ToArray();
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        var keyed = people.Select((person, index) => (Person: person, Key: $"reader{index}")).ToArray();
        var host = await RenderAsync(renderer, Keyed(keyed, log));
        await SettleAsync(renderer);
        await renderer.Dispatcher.InvokeAsync(() => host.SetContent(Keyed(keyed.Reverse().ToArray(), log)));
        await SettleAsync(renderer);
        var before = Enumerable.Range(0, 5).Select(index => log.Renders($"reader{index}")).ToArray();

        // Act
        people[1].FirstName = "Changed";
        await WaitAsync(() => log.Value("reader1") == "Changed");
        await SettleAsync(renderer);

        // Assert
        var renders = Enumerable.Range(0, 5).Select(index => log.Renders($"reader{index}") - before[index]).ToArray();
        Assert.Equal([0, 1, 0, 0, 0], renders);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenARenderStopsReadingAProperty_ThenItsChangesNoLongerRenderAndItIsReleased(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var other = new Person(context) { FirstName = "Other" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        var host = await RenderAsync(renderer, Fragments.Read("reader", person, log, alsoRead: other));
        await SettleAsync(renderer);
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();
        var subscribedBefore = dispatcher.SubscriptionCount;

        // Act
        await renderer.Dispatcher.InvokeAsync(() => host.SetContent(Fragments.Read("reader", person, log)));
        await SettleAsync(renderer);
        var before = log.Renders("reader");
        other.FirstName = "Changed";
        person.FirstName = "Marker";
        await WaitAsync(() => log.Value("reader") == "Marker");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders("reader") - before);
        Assert.Equal(2, subscribedBefore);
        Assert.Equal(1, dispatcher.SubscriptionCount);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAChangeRemovesAComponentThatReadsIt_ThenNothingFails(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Box.Of(builder =>
        {
            // Read here, so that the parent renders on a change as well as the child that it removes.
            if (person.FirstName != "Remove")
            {
                builder.AddContent(0, Fragments.Read("child", person, log));
            }
        }));
        await SettleAsync(renderer);

        // Act
        person.FirstName = "Remove";
        await SettleAsync(renderer);
        person.FirstName = "Back";
        await WaitAsync(() => log.Value("child") == "Back");

        // Assert
        Assert.Empty(renderer.Exceptions);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenARenderThrows_ThenOtherComponentsKeepUpdating(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var failing = new Person(context) { FirstName = "Initial" };
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Both(
            Fragments.Read<ThrowingReader>("failing", failing, log),
            Fragments.Read("reader", person, log)));
        await SettleAsync(renderer);

        // Act
        failing.FirstName = ThrowingReader.FailingValue;
        await WaitAsync(() => renderer.ExceptionCount > 0);
        person.FirstName = "Changed";

        // Assert
        await WaitAsync(() => log.Value("reader") == "Changed");
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAComponentRendersItsValuesAfterAnAsynchronousInitialization_ThenTheyUpdate(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Read<AsyncInitializedReader>("reader", person, log));
        await WaitAsync(() => log.Value("reader") == "Initial");
        await SettleAsync(renderer);

        // Act
        person.FirstName = "Changed";

        // Assert
        await WaitAsync(() => log.Value("reader") == "Changed");
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenTheOwnerOfSectionContentRendersAgain_ThenTheComponentsInTheSectionKeepTheirState(TestRenderMode mode)
    {
        // Arrange: the outlet keys what it renders by the content, so content that changes identity is created anew.
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();
        RenderFragment sectionContent = builder =>
        {
            builder.OpenComponent<InitializationCounter>(0);
            builder.AddComponentParameter(1, nameof(InitializationCounter.Log), log);
            builder.CloseComponent();
        };

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Both(
            builder =>
            {
                builder.OpenComponent<SectionOutlet>(0);
                builder.AddComponentParameter(1, nameof(SectionOutlet.SectionName), "head");
                builder.CloseComponent();
            },
            Box.Of(builder =>
            {
                builder.AddContent(0, person.FirstName);
                log.Record("owner", person.FirstName);
                builder.OpenComponent<SectionContent>(1);
                builder.AddComponentParameter(2, nameof(SectionContent.SectionName), "head");
                builder.AddComponentParameter(3, nameof(SectionContent.ChildContent), sectionContent);
                builder.CloseComponent();
            })));
        await WaitAsync(() => log.Renders(InitializationCounter.Key) == 1);
        await SettleAsync(renderer);

        // Act
        person.FirstName = "Changed";
        await WaitAsync(() => log.Value("owner") == "Changed");
        person.FirstName = "Again";
        await WaitAsync(() => log.Value("owner") == "Again");
        await SettleAsync(renderer);

        // Assert
        Assert.Equal(1, log.Renders(InitializationCounter.Key));
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenAPropertyIsWrittenOnAnotherThread_ThenTheRenderDoesNotRunInsideTheWrite(TestRenderMode mode)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Fragments.Read("reader", person, log));
        await SettleAsync(renderer);
        var before = log.Renders("reader");

        // Act
        var rendersDuringWrite = -1;
        var writer = new Thread(() =>
        {
            person.FirstName = "Changed";
            rendersDuringWrite = log.Renders("reader") - before;
        });
        writer.Start();
        writer.Join();
        await WaitAsync(() => log.Value("reader") == "Changed");

        // Assert
        Assert.Equal(0, rendersDuringWrite);
    }

    [Theory]
    [InlineData(TestRenderMode.WebAssembly)]
    [InlineData(TestRenderMode.Server)]
    public async Task WhenCascadedContentIsRenderedByATrackedComponent_ThenThatComponentUpdates(TestRenderMode mode)
    {
        // Arrange: a fixed cascading value never notifies its subscribers, so only the component that renders the
        // content can keep it current.
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();
        var header = Fragments.Inline("cascaded", person, log);

        await using var renderer = CreateRenderer(mode);
        await RenderAsync(renderer, Box.Of(builder =>
        {
            builder.OpenComponent<CascadingValue<RenderFragment>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<RenderFragment>.Value), header);
            builder.AddComponentParameter(2, nameof(CascadingValue<RenderFragment>.IsFixed), true);
            builder.AddComponentParameter(3, nameof(CascadingValue<RenderFragment>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<CascadedContentRenderer>(0);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }));
        await WaitAsync(() => log.Value("cascaded") == "Initial");
        await SettleAsync(renderer);

        // Act
        person.FirstName = "Changed";

        // Assert
        await WaitAsync(() => log.Value("cascaded") == "Changed");
        person.FirstName = "Again";
        await WaitAsync(() => log.Value("cascaded") == "Again");
    }

    [Fact]
    public async Task WhenAStaticRendererUsesTheRootServiceProvider_ThenItRendersWithoutTheScopedDispatcher()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Static" };
        var log = new RenderLog();
        await using var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>))
            .AddComponentTracking()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var htmlRenderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());

        // Act
        var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await htmlRenderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Content)] = Fragments.Read("reader", person, log)
            }));
            return output.ToHtmlString();
        });

        // Assert
        Assert.Equal("Static", html);
    }

    private static RenderFragment Keyed((Person Person, string Key)[] readers, RenderLog log) => builder =>
    {
        foreach (var (person, key) in readers)
        {
            builder.OpenComponent<Reader>(0);
            builder.SetKey(person);
            builder.AddComponentParameter(1, nameof(Reader.Key), key);
            builder.AddComponentParameter(2, nameof(Reader.Person), person);
            builder.AddComponentParameter(3, nameof(Reader.Log), log);
            builder.CloseComponent();
        }
    };

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();
    }

    private static TestRenderer CreateRenderer(TestRenderMode mode)
    {
        var jsRuntime = new BrowserlessJSRuntime();
        var renderer = new TestRenderer(mode, configureServices: services => services
            .AddSingleton<IJSRuntime>(jsRuntime)
            .AddSingleton<IErrorBoundaryLogger, NullErrorBoundaryLogger>()
            .AddComponentTracking(options => options.ThrottleMilliseconds = 0));
        jsRuntime.Dispatcher = renderer.Dispatcher;
        return renderer;
    }

    private static Task<Host> RenderAsync(TestRenderer renderer, RenderFragment content)
    {
        return renderer.RenderAsync<Host>(new Dictionary<string, object?> { [nameof(Host.Content)] = content });
    }

    private static Task WaitAsync(Func<bool> condition)
    {
        return AsyncTestHelpers.WaitUntilAsync(condition, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(5));
    }

    private static Task SettleAsync(TestRenderer renderer)
    {
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();
        return WaitAsync(() => dispatcher.IsIdle && renderer.UnacknowledgedBatchCount == 0);
    }

    /// <summary>
    /// Answers every call with the default value, and reports to a virtualized list that its first spacer is visible in a
    /// container of 500 pixels, as the script of a browser does after the list has rendered.
    /// </summary>
    private sealed class BrowserlessJSRuntime : IJSRuntime
    {
        public Dispatcher? Dispatcher { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "Blazor._internal.Virtualize.init" && args?[0] is { } reference && Dispatcher is { } dispatcher)
            {
                // The test reaches into the interop object as the script does, through its invokable method.
                var callbacks = reference.GetType().GetProperty("Value")!.GetValue(reference)!;
                var method = callbacks.GetType().GetMethod("OnSpacerBeforeVisible")!;
                _ = Task.Run(() => dispatcher.InvokeAsync(() => method.Invoke(callbacks, [0f, 0f, 500f])));
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class NullErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }
}

/// <summary>Renders a template for each item, as a list or a grid does.</summary>
public sealed class TemplatedList : ComponentBase
{
    [Parameter]
    public IReadOnlyList<Person> Items { get; set; } = [];

    [Parameter]
    public RenderFragment<Person>? ItemTemplate { get; set; }

    [Parameter]
    public RenderLog Log { get; set; } = null!;

    public static RenderFragment Of(IReadOnlyList<Person> items, RenderLog log, RenderFragment<Person> template) => builder =>
    {
        builder.OpenComponent<TemplatedList>(0);
        builder.AddComponentParameter(1, nameof(Items), items);
        builder.AddComponentParameter(2, nameof(ItemTemplate), template);
        builder.AddComponentParameter(3, nameof(Log), log);
        builder.CloseComponent();
    };

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        Log.Record("list", null);
        foreach (var item in Items)
        {
            builder.AddContent(0, ItemTemplate?.Invoke(item));
        }
    }
}

/// <summary>Counts how often it is created, which it is again when the component that renders it disposes it.</summary>
public sealed class InitializationCounter : ComponentBase
{
    public const string Key = "initialized";

    [Parameter]
    public RenderLog Log { get; set; } = null!;

    protected override void OnInitialized() => Log.Record(Key, null);
}

/// <summary>Renders the content that a cascading value provides, as a header slot of a layout does.</summary>
public sealed class CascadedContentRenderer : ComponentBase
{
    [CascadingParameter]
    public RenderFragment? Header { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Header);
}

/// <summary>Fails to render while the first name has <see cref="FailingValue"/>.</summary>
public sealed class ThrowingReader : Reader
{
    public const string FailingValue = "Throw";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Person.FirstName == FailingValue)
        {
            throw new InvalidOperationException("The render fails.");
        }

        base.BuildRenderTree(builder);
    }
}

/// <summary>Renders its value only after an asynchronous initialization, as a component that loads data first does.</summary>
public sealed class AsyncInitializedReader : Reader
{
    private bool _isLoaded;

    protected override async Task OnInitializedAsync()
    {
        await Task.Yield();
        _isLoaded = true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_isLoaded)
        {
            base.BuildRenderTree(builder);
        }
    }
}
