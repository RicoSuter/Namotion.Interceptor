using Bunit;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Host.Components.Browser;
using HomeBlaze.Host.Preferences;
using HomeBlaze.Host.Services.Display;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Storage.Blazor.Files;
using HomeBlaze.Storage.Blazor.Tests;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Blazor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Tests.Access;

/// <summary>A subject that holds other subjects, whose number the property panel shows next to it.</summary>
[InterceptorSubject]
public partial class CountTestBox
{
    public CountTestBox()
    {
        Items = [];
    }

    public partial Dictionary<string, IInterceptorSubject> Items { get; set; }
}

/// <summary>A subject held by a <see cref="CountTestBox"/>.</summary>
[InterceptorSubject]
public partial class CountTestItem
{
    [State]
    public partial string? Name { get; set; }
}

/// <summary>
/// The number next to a child in the property panel counts only what the viewer may see, and stays current when the
/// permissions or the graph change, as the application tracks the reads of every render.
/// </summary>
public sealed class PanelChildCountTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();
    private PageTestGraph _graph = null!;
    private CountTestBox _box = null!;
    private CountTestItem _first = null!;

    public PanelChildCountTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddBlazorSubjectTracking(options => options.ThrottleMilliseconds = 0);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton(new SubjectComponentRegistry(new TypeProvider().AddAssembly(typeof(MarkdownFileEditComponent).Assembly)));
        Services.AddSingleton<PropertyRowLayoutService>();
        Services.AddSingleton<MetadataLinkResolver>();
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        Services.AddSingleton<BrowserPreferences>();
    }

    public async Task InitializeAsync()
    {
        _graph = await PageTestGraph.CreateAsync("Some text.");
        _first = new CountTestItem { Name = "First" };
        _box = new CountTestBox { Items = new Dictionary<string, IInterceptorSubject> { ["first"] = _first, ["second"] = new CountTestItem { Name = "Second" } } };
        _graph.Storage.Children = new Dictionary<string, IInterceptorSubject>(_graph.Storage.Children) { ["box"] = _box };
        var rootManager = _graph.Services.GetRequiredService<RootManager>();
        rootManager.Root = _graph.Storage;
        Services.AddSingleton(rootManager);
        Services.AddSingleton(_graph.Services.GetRequiredService<SubjectPathResolver>());

        // After the last service: the tracking of the reads of a render asks whether the renderer is interactive.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public void WhenTheViewerLosesSightOfAGrandchild_ThenTheCountOfItsParentDrops()
    {
        // Arrange
        var panel = RenderPanel();
        Assert.Equal("2", GetBoxCount(panel));

        // Act
        _permissions.CannotShow.Add(_first);
        _permissions.RaiseChanged();

        // Assert
        panel.WaitForAssertion(() => Assert.Equal("1", GetBoxCount(panel)));
    }

    [Fact]
    public void WhenAGrandchildIsAdded_ThenTheCountOfItsParentGrows()
    {
        // Arrange
        var panel = RenderPanel();
        Assert.Equal("2", GetBoxCount(panel));

        // Act
        _box.Items = new Dictionary<string, IInterceptorSubject>(_box.Items) { ["third"] = new CountTestItem { Name = "Third" } };

        // Assert
        panel.WaitForAssertion(() => Assert.Equal("3", GetBoxCount(panel)));
    }

    private IRenderedComponent<SubjectPropertyPanel> RenderPanel() =>
        Render<SubjectPropertyPanel>(parameters => parameters
            .Add(panel => panel.Subject, _graph.Storage)
            .Add(panel => panel.SubjectRegistry, ((IInterceptorSubject)_graph.Storage).Context.GetService<ISubjectRegistry>()));

    private static string? GetBoxCount(IRenderedComponent<SubjectPropertyPanel> panel) =>
        panel.FindAll(".subject-pane-child-name")
            .Single(name => name.TextContent.Contains("box", StringComparison.OrdinalIgnoreCase))
            .ParentElement?.ParentElement?.QuerySelector(".mud-chip")?.TextContent.Trim();
}
