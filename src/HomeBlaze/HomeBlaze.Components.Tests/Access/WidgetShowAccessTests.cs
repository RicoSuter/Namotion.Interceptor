using System.Text.RegularExpressions;
using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// A widget renders only when its subject is shown, and a widget shows no subject that the viewer may not see: not the one a
/// reference leads to, not a row of a table and not the bound subject of a text.
/// </summary>
public sealed partial class WidgetShowAccessTests : BunitContext, IAsyncLifetime
{
    private const string Text = "VISIBLE-VALUE-7731";
    private const string PathPlaceholder = "{path}";

    private readonly TestUserPermissions _permissions = new();
    private readonly WidgetTestGraph _graph;

    public WidgetShowAccessTests()
    {
        _graph = WidgetTestGraph.Create(this, Text);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _graph.DisposeAsync();
    }

    [Fact]
    public void WhenTheViewerMayNotSeeTheSubjectOfAWidget_ThenItRendersAsForNoSubject()
    {
        // Arrange
        var widget = _graph.CreateWidget(typeof(ValueTile), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text)));
        var shown = _graph.RenderWidget(widget).Markup;
        _permissions.CannotShow.Add(widget);

        // Act
        var hidden = _graph.RenderWidget(widget).Markup;
        var none = RenderWithoutSubject();

        // Assert
        Assert.Equal(none, hidden);
        Assert.Contains(Text, shown);
    }

    [Fact]
    public void WhenTheViewerLosesSightOfTheSubjectOfAWidget_ThenTheWidgetRendersAgainAsForNoSubject()
    {
        // Arrange
        var widget = _graph.CreateWidget(typeof(ValueTile), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text)));
        var rendered = _graph.RenderWidget(widget);
        Assert.Contains(Text, rendered.Markup);

        // Act
        _permissions.CannotShow.Add(widget);
        _permissions.RaiseChanged();

        // Assert
        rendered.WaitForAssertion(() => Assert.Equal(RenderWithoutSubject(), rendered.Markup));
    }

    [Fact]
    public void WhenAReferenceLeadsToASubjectTheViewerMayNotSee_ThenItRendersAsForAPathThatLeadsNowhere()
    {
        // Arrange
        var path = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Lock));
        var shown = RenderReference(path);
        _permissions.CannotShow.Add(_graph.Device.Lock!);

        // Act
        var hidden = RenderReference(path);
        var missing = RenderReference(WidgetTestGraph.MissingPath);

        // Assert
        Assert.Equal(missing, hidden);
        Assert.NotEqual(missing, shown);
    }

    [Fact]
    public void WhenATableListsASubjectTheViewerMayNotSee_ThenItHasNoRowForIt()
    {
        // Arrange
        _permissions.CannotShow.Add(_graph.Device.Devices[0]);
        var table = _graph.CreateWidget(typeof(SubjectTable), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Devices)));

        // Act
        var rendered = _graph.RenderWidget(table).Markup;

        // Assert
        Assert.DoesNotContain("Pump", rendered);
        Assert.Contains("Fan", rendered);
    }

    [Fact]
    public void WhenATextShowsTheBoundSubjectAndTheViewerMayNotSeeIt_ThenTheSubjectIsMissing()
    {
        // Arrange
        var subjectName = typeof(AccessTestDevice).FullName!;
        var tile = (ValueTile)_graph.CreateWidget(typeof(ValueTile), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text)));
        tile.Label = "Owner {{ subject }}";
        var shown = _graph.RenderWidget(tile).Markup;
        _permissions.CannotShow.Add(_graph.Device);

        // Act
        var hidden = _graph.RenderWidget(tile).Markup;

        // Assert
        Assert.Contains(subjectName, shown);
        Assert.DoesNotContain(subjectName, hidden);
        Assert.Contains("Owner", hidden);
    }

    [Fact]
    public void WhenATabIsHidden_ThenTheTabLayoutDoesNotNameIt()
    {
        // Arrange
        var layout = _graph.CreateSubject<TabLayout>();
        var shownTab = _graph.CreateSubject<Tab>();
        shownTab.Title = "Open tab";
        var hiddenTab = _graph.CreateSubject<Tab>();
        hiddenTab.Title = "Secret tab";
        layout.Tabs = [shownTab, hiddenTab];
        _graph.Device.Widget = layout;
        _permissions.CannotShow.Add(hiddenTab);

        // Act
        var rendered = _graph.RenderWidget(layout);

        // Assert
        Assert.Contains("Open tab", rendered.Markup);
        Assert.DoesNotContain("Secret tab", rendered.Markup);
    }

    [Fact]
    public void WhenAnExpansionPanelIsHidden_ThenTheExpansionPanelsDoNotNameIt()
    {
        // Arrange
        var layout = _graph.CreateSubject<ExpansionPanels>();
        var shownPanel = _graph.CreateSubject<ExpansionPanel>();
        shownPanel.Title = "Open panel";
        var hiddenPanel = _graph.CreateSubject<ExpansionPanel>();
        hiddenPanel.Title = "Secret panel";
        layout.Panels = [shownPanel, hiddenPanel];
        _graph.Device.Widget = layout;
        _permissions.CannotShow.Add(hiddenPanel);

        // Act
        var rendered = _graph.RenderWidget(layout);

        // Assert
        Assert.Contains("Open panel", rendered.Markup);
        Assert.DoesNotContain("Secret panel", rendered.Markup);
    }

    [Fact]
    public void WhenTheProcessOfAHostSystemIsHidden_ThenTheHostSystemWidgetDoesNotShowIt()
    {
        // Arrange
        var host = _graph.CreateSubject<HostSystem>();
        _graph.Device.Widget = host;
        var shown = _graph.RenderWidget(host).Markup;
        _permissions.CannotShow.Add(host.Process);

        // Act
        var hidden = _graph.RenderWidget(host).Markup;

        // Assert
        Assert.Contains("Process:", shown);
        Assert.DoesNotContain("Process:", hidden);
        Assert.Contains("Uptime", hidden);
    }

    private string RenderWithoutSubject() =>
        Render<SubjectComponent>(parameters => parameters
            .Add(component => component.Subject, null)
            .Add(component => component.Type, SubjectComponentType.Widget)).Markup;

    private string RenderReference(string path)
    {
        var widget = _graph.CreateWidget(typeof(Widget), path);
        return Normalize(_graph.RenderWidget(widget).Markup, path);
    }

    // The path shows where a subject is missing, and the identifiers MudBlazor and the renderer generate differ from render to render.
    private static string Normalize(string markup, string path) =>
        GeneratedIdentifier().Replace(markup.Replace(path, PathPlaceholder, StringComparison.Ordinal), "$1=\"\"");

    [GeneratedRegex("(?<=\\s)(id|for|aria-labelledby|aria-describedby|aria-controls|blazor:[A-Za-z]+)=\"[^\"]*\"")]
    private static partial Regex GeneratedIdentifier();
}
