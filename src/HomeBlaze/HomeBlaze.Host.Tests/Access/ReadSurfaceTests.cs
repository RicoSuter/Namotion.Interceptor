using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Host.Components.Browser;
using HomeBlaze.Host.Components.Dialogs;
using HomeBlaze.Host.Components.Layout;
using HomeBlaze.Host.Preferences;
using HomeBlaze.Host.Services.Display;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Blazor.Files;
using HomeBlaze.Storage.Blazor.Tests;
using HomeBlaze.Storage.Files;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Tests.Access;

/// <summary>A history store that knows no samples, so that the property panel offers the history of recorded states.</summary>
[InterceptorSubject]
public partial class EmptyHistoryStore : IHistoryStore
{
    public int Priority => 0;

    public ImmutableArray<HistoryCoverage> CoverageRanges => [];

    public IReadOnlySet<string> SupportedAggregations { get; } = new HashSet<string>();

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        ValueTask.FromResult<HistoryPoint?>(null);

    public ValueTask<HistoryPoint?> GetLastNumericSampleBeforeAsync(string propertyPath, DateTimeOffset before, CancellationToken cancellationToken) =>
        ValueTask.FromResult<HistoryPoint?>(null);
}

/// <summary>
/// The navigation, the subject browser, the property panel and the tag filter show only the subjects, members and history
/// the viewer may see.
/// </summary>
public sealed partial class ReadSurfaceTests : BunitContext, IAsyncLifetime
{
    private const string Tag = "kitchen";

    private readonly TestUserPermissions _permissions = new();
    private PageTestGraph _graph = null!;
    private JsonFile _data = null!;
    private MarkdownFile _otherPage = null!;

    public ReadSurfaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton(new SubjectComponentRegistry(new TypeProvider().AddAssembly(typeof(MarkdownFileEditComponent).Assembly)));
        Services.AddSingleton<PropertyRowLayoutService>();
        Services.AddSingleton<MetadataLinkResolver>();
        Services.AddSingleton<NavigationItemResolver>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        Services.AddSingleton<BrowserPreferences>();
        AddBunitPersistentComponentState();
    }

    public async Task InitializeAsync()
    {
        _graph = await PageTestGraph.CreateAsync("Some text.");
        _otherPage = await _graph.AddPageAsync("other.md", "Other text.");
        _data = await _graph.AddJsonFileAsync("data.json", "{}");
        var rootManager = _graph.Services.GetRequiredService<RootManager>();
        rootManager.Root = _graph.Storage;
        Services.AddSingleton(rootManager);
        Services.AddSingleton(_graph.Services.GetRequiredService<SubjectPathResolver>());
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private MarkdownFile Page => _graph.Page;

    [Fact]
    public void WhenTheViewerMayNotSeeAPage_ThenTheNavigationMenuDoesNotListIt()
    {
        // Arrange
        _permissions.CannotShow.Add(Page);

        // Act
        var menu = RenderNavigationMenu();

        // Assert
        var links = GetLinks(menu);
        Assert.DoesNotContain(links, link => link.Contains("page.md", StringComparison.Ordinal));
        Assert.Contains(links, link => link.Contains("other.md", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheViewerMaySeeNothing_ThenTheNavigationMenuListsOnlyTheBrowser()
    {
        // Arrange
        Services.AddSingleton<IUserPermissions>(DenyAllPermissions.Instance);

        // Act
        var menu = RenderNavigationMenu();

        // Assert
        Assert.Equal(["browser"], GetLinks(menu));
    }

    [Fact]
    public void WhenTheViewerLosesSightOfAPage_ThenTheNavigationMenuDropsIt()
    {
        // Arrange
        var menu = RenderNavigationMenu();
        Assert.Contains(GetLinks(menu), link => link.Contains("page.md", StringComparison.Ordinal));

        // Act
        _permissions.CannotShow.Add(Page);
        _permissions.RaiseChanged();

        // Assert
        menu.WaitForAssertion(() => Assert.DoesNotContain(GetLinks(menu), link => link.Contains("page.md", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhenTheViewerMayNotReadAMember_ThenThePropertyPanelDoesNotListIt()
    {
        // Arrange
        _permissions.CannotRead.Add(_graph.DoorIsOpen);

        // Act
        var panel = RenderPanel(_graph.Door);

        // Assert
        var names = GetValueNames(panel);
        Assert.DoesNotContain(_graph.DoorIsOpen.GetDisplayName(), names);
        Assert.Contains(_graph.DoorIsLocked.GetDisplayName(), names);
    }

    [Fact]
    public void WhenTheViewerMayNotReadTheHistoryOfAMember_ThenThePropertyPanelOffersNoHistoryOfIt()
    {
        // Arrange
        _ = new EmptyHistoryStore(((IInterceptorSubject)_graph.Storage).Context);
        _permissions.CannotReadHistory.Add(_graph.DoorIsOpen);

        // Act
        var panel = RenderPanel(_graph.Door);

        // Assert
        var historyLinks = panel.FindAll("[data-testid='subject-pane-value'] [title='Show history']").Select(link => link.TextContent).ToList();
        Assert.DoesNotContain(historyLinks, text => text.Contains(_graph.DoorIsOpen.GetDisplayName(), StringComparison.Ordinal));
        Assert.Contains(historyLinks, text => text.Contains(_graph.DoorIsLocked.GetDisplayName(), StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheViewerMayNotReadAnOperation_ThenThePropertyPanelDoesNotListIt()
    {
        // Arrange
        var createMarkdownFile = ((IInterceptorSubject)_graph.Storage).TryGetMethodMember(nameof(FluentStorageContainer.CreateMarkdownFileAsync))!;
        _permissions.CannotRead.Add(createMarkdownFile);

        // Act
        var panel = RenderPanel(_graph.Storage);

        // Assert
        var buttons = panel.FindAll("button.subject-pane-method").Select(button => button.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Create Markdown File", buttons);
        Assert.Contains("Create", buttons);
    }

    [Fact]
    public void WhenTheViewerMayNotSeeAChild_ThenThePropertyPanelDoesNotListIt()
    {
        // Arrange
        _permissions.CannotShow.Add(Page);

        // Act
        var panel = RenderPanel(_graph.Storage);

        // Assert
        var children = panel.FindAll(".subject-pane-child-name").Select(child => child.TextContent).ToList();
        Assert.DoesNotContain("Page", children);
        Assert.Contains("Other", children);
    }

    [Fact]
    public void WhenTheViewerMayNotSeeASubject_ThenItsPaneInTheBrowserRendersAsForAPathThatLeadsNowhere()
    {
        // Arrange
        _permissions.CannotShow.Add(Page);

        // Act
        var hidden = Normalize(RenderBrowser("page.md").Markup);
        var missing = Normalize(RenderBrowser("nothing.md").Markup);
        var shown = Normalize(RenderBrowser("other.md").Markup);

        // Assert
        Assert.Equal(missing, hidden);
        Assert.NotEqual(missing, shown);
    }

    [Fact]
    public void WhenTheViewerMayNotSeeTheRoot_ThenTheBrowserShowsNoPane()
    {
        // Arrange
        var shown = RenderBrowser("other.md").FindAll("[data-testid='subject-pane']").Count;
        _permissions.CannotShow.Add(_graph.Storage);

        // Act
        var browser = RenderBrowser("other.md");

        // Assert
        Assert.Empty(browser.FindAll("[data-testid='subject-pane']"));
        Assert.Equal(2, shown);
    }

    [Fact]
    public void WhenTheViewerLosesSightOfAShownSubject_ThenTheBrowserClosesItsPane()
    {
        // Arrange
        var browser = RenderBrowser("page.md");
        Assert.Equal(2, browser.FindAll("[data-testid='subject-pane']").Count);

        // Act
        _permissions.CannotShow.Add(Page);
        _permissions.RaiseChanged();

        // Assert
        browser.WaitForAssertion(() => Assert.Single(browser.FindAll("[data-testid='subject-pane']")));
    }

    [Fact]
    public async Task WhenTheViewerMayNotSeeATaggedSubject_ThenTheTagFilterDoesNotListIt()
    {
        // Arrange
        SetTag(Page);
        SetTag(_data);
        _permissions.CannotShow.Add(Page);
        var provider = Render<MudDialogProvider>();

        // Act
        await provider.InvokeAsync(() => MetadataTagFilterDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), Tag));

        // Assert
        var items = provider.FindAll("[data-testid='metadata-tag-filter-item']").Select(item => item.TextContent).ToList();
        Assert.DoesNotContain(items, item => item.Contains("/page.md", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Contains("/data.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenAChildTheViewerMayNotSeeIsBetweenTheReorderedChildren_ThenItKeepsItsPlace()
    {
        // Arrange
        _permissions.CannotShow.Add(_otherPage);
        var panel = RenderPanel(_graph.Storage);
        await panel.Find("[data-testid='reorder-children-button']").ClickAsync(new MouseEventArgs());
        Assert.Equal(["data.json", "other.md", "page.md"], ChildOrdering.GetShownNames(_graph.Storage));

        // Act
        await panel.InvokeAsync(() => panel.Instance.OnChildDropped("page.md", "data.json"));

        // Assert
        var written = JsonDocument.Parse(await _graph.ReadTextAsync(".metadata.json")).RootElement.GetProperty(MetadataKeys.Order);
        Assert.Equal(["page.md", "other.md", "data.json"], written.EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public void WhenAMetadataLinkLeadsToASubjectTheViewerMayNotSee_ThenTheSummaryRendersItLikeALinkThatLeadsNowhere()
    {
        // Arrange
        var shown = RenderSummary("/other.md");
        _permissions.CannotShow.Add(_otherPage);

        // Act
        var hidden = RenderSummary("/other.md");
        var missing = RenderSummary("/nothing.md");

        // Assert
        Assert.Equal(missing, hidden);
        Assert.NotEqual(missing, shown);
    }

    [Fact]
    public void WhenAMetadataLinkLeadsToAPropertyTheViewerMayNotRead_ThenTheSummaryRendersItLikeALinkThatLeadsNowhere()
    {
        // Arrange
        var doorPath = _graph.Services.GetRequiredService<SubjectPathResolver>().GetPath(_graph.Door, PathStyle.Canonical);
        var shown = RenderSummary($"{doorPath}/{nameof(TestDoor.IsOpen)}");
        _permissions.CannotRead.Add(_graph.DoorIsOpen);

        // Act
        var hidden = RenderSummary($"{doorPath}/{nameof(TestDoor.IsOpen)}");
        var missing = RenderSummary($"{doorPath}/Nothing");

        // Assert
        Assert.Equal(missing, hidden);
        Assert.NotEqual(missing, shown);
    }

    [Fact]
    public async Task WhenAMetadataLinkInTheSummaryDialogLeadsToASubjectTheViewerMayNotSee_ThenItRendersLikeALinkThatLeadsNowhere()
    {
        // Arrange
        _permissions.CannotShow.Add(_otherPage);
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        // Act
        await provider.InvokeAsync(() => MetadataSummaryDialog.ShowAsync(dialogService, "Hidden", CreateLinkMetadata("/other.md"), edit: null));
        await provider.InvokeAsync(() => MetadataSummaryDialog.ShowAsync(dialogService, "Missing", CreateLinkMetadata("/nothing.md"), edit: null));
        await provider.InvokeAsync(() => MetadataSummaryDialog.ShowAsync(dialogService, "Shown", CreateLinkMetadata("/data.json"), edit: null));

        // Assert
        var summaries = provider.FindAll("[data-testid='metadata-summary']");
        Assert.Equal(3, summaries.Count);
        Assert.Equal(Normalize(summaries[1].OuterHtml.Replace("/nothing.md", "{path}")), Normalize(summaries[0].OuterHtml.Replace("/other.md", "{path}")));
        Assert.DoesNotContain("Broken link", summaries[2].OuterHtml);
        Assert.Contains("Broken link", summaries[0].OuterHtml);
    }

    [Fact]
    public void WhenTheViewerMayNotReadTheMetadataOfASubject_ThenThePropertyPanelShowsNoneOfIt()
    {
        // Arrange
        SetTag(Page);
        var shown = RenderPanel(Page).FindAll("[data-testid='metadata-tag']").Count;
        _permissions.CannotRead.Add(Page.TryGetRegisteredSubject()!.TryGetProperty(KnownAttributes.Metadata)!);

        // Act
        var panel = RenderPanel(Page);

        // Assert
        Assert.Empty(panel.FindAll("[data-testid='metadata-tag']"));
        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task WhenTheViewerMayNotReadTheMetadataOfATaggedSubject_ThenTheTagFilterDoesNotListIt()
    {
        // Arrange
        SetTag(Page);
        SetTag(_data);
        _permissions.CannotRead.Add(Page.TryGetRegisteredSubject()!.TryGetProperty(KnownAttributes.Metadata)!);
        var provider = Render<MudDialogProvider>();

        // Act
        await provider.InvokeAsync(() => MetadataTagFilterDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), Tag));

        // Assert
        var items = provider.FindAll("[data-testid='metadata-tag-filter-item']").Select(item => item.TextContent).ToList();
        Assert.DoesNotContain(items, item => item.Contains("/page.md", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Contains("/data.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheViewerMayWriteButNotReadTheMetadataOfASubject_ThenTheEditDialogShowsNoneOfIt()
    {
        // Arrange
        SetMetadata(Page, ImmutableDictionary<string, JsonElement>.Empty.Add(MetadataKeys.Notes, JsonSerializer.SerializeToElement("Hidden note")));
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => MetadataEditDialog.ShowAsync(dialogService, Page, "Readable"));
        _permissions.CannotRead.Add(Page.TryGetRegisteredSubject()!.TryGetProperty(KnownAttributes.Metadata)!);

        // Act
        await provider.InvokeAsync(() => MetadataEditDialog.ShowAsync(dialogService, Page, "Hidden"));

        // Assert
        var dialogs = provider.FindAll("[data-testid='metadata-edit-dialog']");
        Assert.Equal(2, dialogs.Count);
        Assert.Contains("Hidden note", dialogs[0].OuterHtml);
        Assert.DoesNotContain("Hidden note", dialogs[1].OuterHtml);
    }

    [Fact]
    public async Task WhenATagIsOnlyOnASubjectTheViewerMayNotSee_ThenTheEditDialogDoesNotSuggestIt()
    {
        // Arrange
        SetMetadata(_otherPage, ImmutableDictionary<string, JsonElement>.Empty.Add(MetadataKeys.Tags, JsonSerializer.SerializeToElement(new[] { "hidden-tag" })));
        SetTag(_data);
        _permissions.CannotShow.Add(_otherPage);
        var provider = Render<MudDialogProvider>();

        // Act
        await provider.InvokeAsync(() => MetadataEditDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), Page, "Metadata"));

        // Assert
        var search = provider.FindComponent<MudAutocomplete<string>>().Instance.SearchFunc;
        Assert.NotNull(search);
        var found = search(string.Empty, CancellationToken.None);
        Assert.NotNull(found);
        var suggestions = (await found).ToList();
        Assert.DoesNotContain("hidden-tag", suggestions);
        Assert.Contains(Tag, suggestions);
    }

    private string RenderSummary(string path) =>
        Normalize(Render<MetadataSummary>(parameters => parameters.Add(summary => summary.Metadata, CreateLinkMetadata(path))).Markup
            .Replace(path, "{path}", StringComparison.Ordinal));

    private static ImmutableDictionary<string, JsonElement> CreateLinkMetadata(string path) =>
        ImmutableDictionary<string, JsonElement>.Empty.Add(MetadataKeys.Links, JsonSerializer.SerializeToElement(new[] { new { path } }));

    private static void SetTag(IInterceptorSubject subject) =>
        SetMetadata(subject, ImmutableDictionary<string, JsonElement>.Empty.Add(MetadataKeys.Tags, JsonSerializer.SerializeToElement(new[] { Tag })));

    private static void SetMetadata(IInterceptorSubject subject, ImmutableDictionary<string, JsonElement> values) =>
        subject.TryGetRegisteredSubject()!.AddProperty(KnownAttributes.Metadata, values.GetType(), _ => values, null);

    private IRenderedComponent<NavMenu> RenderNavigationMenu() =>
        Render<NavMenu>(parameters => parameters.Add(menu => menu.Root, _graph.Storage));

    private static List<string> GetLinks(IRenderedComponent<NavMenu> menu) =>
        menu.FindAll("a[href]").Select(link => link.GetAttribute("href")!).ToList();

    private static List<string> GetValueNames(IRenderedComponent<SubjectPropertyPanel> panel) =>
        panel.FindAll("[data-testid='subject-pane-value-name'], [data-testid='subject-pane-empty-values']")
            .Select(name => name.TextContent.Trim())
            .ToList();

    private IRenderedComponent<SubjectPropertyPanel> RenderPanel(IInterceptorSubject subject) =>
        Render<SubjectPropertyPanel>(parameters => parameters
            .Add(panel => panel.Subject, subject)
            .Add(panel => panel.SubjectRegistry, subject.Context.GetService<ISubjectRegistry>()));

    private IRenderedComponent<SubjectBrowser> RenderBrowser(string path) =>
        Render<SubjectBrowser>(parameters => parameters.Add(browser => browser.Path, path));

    // The identifiers MudBlazor and the renderer generate differ from render to render.
    private static string Normalize(string markup) => GeneratedIdentifier().Replace(markup, "$1=\"\"");

    [GeneratedRegex("(?<=\\s)(id|for|aria-labelledby|aria-describedby|aria-controls|blazor:[A-Za-z]+)=\"[^\"]*\"")]
    private static partial Regex GeneratedIdentifier();
}
