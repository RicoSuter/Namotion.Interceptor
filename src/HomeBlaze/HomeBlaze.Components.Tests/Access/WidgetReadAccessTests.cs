using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

public sealed partial class WidgetReadAccessTests : BunitContext, IAsyncLifetime
{
    private const string Text = "VISIBLE-VALUE-4410";
    private const string PathPlaceholder = "{path}";

    private const string LayoutReason = "Pure layout: it shows the widgets inside it and binds no path.";
    private const string ValueMapReason = "Shows nothing on a viewed page: its cases are evaluated by the widgets that refer to the map.";

    // How each widget type is bound to a member of the root, so that the widget can show it when it may. Without a binder,
    // every path argument of the widget is set to the path.
    private static readonly Dictionary<Type, WidgetBinding> Bindings = new()
    {
        [typeof(Card)] = new(nameof(AccessTestDevice.Text), static (widget, path) => ((Card)widget).Title = "{{ " + path + " }}"),
        [typeof(Control)] = new(nameof(AccessTestDevice.Text)),
        [typeof(DoorLock)] = new(nameof(AccessTestDevice.Lock)),
        [typeof(Gauge)] = new(nameof(AccessTestDevice.Level)),
        [typeof(Indicator)] = new(nameof(AccessTestDevice.Flag)),
        [typeof(OperationButton)] = new(nameof(AccessTestDevice.Stop)),
        [typeof(Progress)] = new(nameof(AccessTestDevice.Level)),
        [typeof(Select)] = new(nameof(AccessTestDevice.Mode)),
        [typeof(Slider)] = new(nameof(AccessTestDevice.Level)),
        [typeof(SubjectTable)] = new(nameof(AccessTestDevice.Devices)),
        [typeof(Switch)] = new(nameof(AccessTestDevice.Flag)),
        [typeof(ValueRow)] = new(nameof(AccessTestDevice.Text)),
        [typeof(ValueTile)] = new(nameof(AccessTestDevice.Text)),
        [typeof(Widget)] = new(nameof(AccessTestDevice.Lock)),
    };

    // The widget types that bind nothing a viewer could be refused, each with the reason.
    private static readonly Dictionary<Type, string> ExcludedTypes = new()
    {
        [typeof(Alert)] = "Shows a fixed icon and the widget inside it; binds no path.",
        [typeof(CanvasLayout)] = LayoutReason,
        [typeof(ExpansionPanels)] = LayoutReason + " The titles of its panels are plain text.",
        [typeof(FlowLayout)] = LayoutReason,
        [typeof(GridLayout)] = LayoutReason,
        [typeof(HostSystem)] = "Shows the states of its own subject: read granularity inside a widget whose subject is shown is per subject (spec known limit); whether the subject is shown is decided by CanShow, which the sweep of hidden subjects covers.",
        [typeof(Responsive)] = LayoutReason,
        [typeof(TabLayout)] = LayoutReason,
        [typeof(ValueList)] = "Lists value rows, which the sweep covers as ValueRow.",
        [typeof(ValueMap)] = ValueMapReason,
        [typeof(ValueMapCase)] = ValueMapReason,
    };

    private readonly TestUserPermissions _permissions = new();
    private readonly WidgetTestGraph _graph;

    public WidgetReadAccessTests()
    {
        _graph = WidgetTestGraph.Create(this, Text);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public static TheoryData<Type> WidgetSubjectTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in DiscoverWidgetSubjectTypes())
        {
            if (!ExcludedTypes.ContainsKey(type))
            {
                data.Add(type);
            }
        }

        return data;
    }

    // Every widget type, also those that bind nothing: the host renders none of them for a subject the viewer may not see.
    public static TheoryData<Type> AllWidgetSubjectTypes() => [.. DiscoverWidgetSubjectTypes()];

    // The widgets that show the text of a string value.
    public static TheoryData<Type> ValueShowingWidgetTypes() =>
        [typeof(ValueTile), typeof(Gauge), typeof(Indicator), typeof(Control), typeof(ValueRow)];

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _graph.DisposeAsync();
    }

    [Fact]
    public void WhenTheWidgetTypesAreDiscovered_ThenEachIsBoundOrExcludedWithAReason()
    {
        // Act
        var discovered = DiscoverWidgetSubjectTypes();

        // Assert
        Assert.DoesNotContain(discovered, type => !Bindings.ContainsKey(type) && !ExcludedTypes.ContainsKey(type));
        Assert.DoesNotContain(Bindings.Keys.Concat(ExcludedTypes.Keys), type => !discovered.Contains(type));
        Assert.Empty(Bindings.Keys.Intersect(ExcludedTypes.Keys));
    }

    [Theory]
    [MemberData(nameof(WidgetSubjectTypes))]
    public void WhenAWidgetIsBoundToAMemberTheUserMayNotRead_ThenItRendersAsForAPathThatLeadsNowhere(Type widgetType)
    {
        // Arrange
        var binding = GetBinding(widgetType);
        _permissions.CannotRead.Add(_graph.GetProperty(binding.Member));

        // Act
        var hidden = RenderBound(widgetType, WidgetTestGraph.PathOf(binding.Member));
        var missing = RenderBound(widgetType, WidgetTestGraph.MissingPath);

        // Assert
        Assert.Equal(missing, hidden);
    }

    [Theory]
    [MemberData(nameof(WidgetSubjectTypes))]
    public void WhenAWidgetIsBoundToAMemberTheUserMayRead_ThenItRendersOtherwiseThanForAPathThatLeadsNowhere(Type widgetType)
    {
        // Arrange
        var binding = GetBinding(widgetType);

        // Act
        var readable = RenderBound(widgetType, WidgetTestGraph.PathOf(binding.Member));
        var missing = RenderBound(widgetType, WidgetTestGraph.MissingPath);

        // Assert
        Assert.NotEqual(missing, readable);
    }

    [Theory]
    [MemberData(nameof(AllWidgetSubjectTypes))]
    public void WhenTheUserMayNotSeeTheSubjectOfAWidget_ThenItRendersAsForNoSubject(Type widgetType)
    {
        // Arrange
        var widget = CreateAnyWidget(widgetType);
        _permissions.CannotShow.Add(widget);

        // Act
        var hidden = _graph.RenderWidget(widget).Markup;
        var none = Render<SubjectComponent>(parameters => parameters
            .Add(component => component.Subject, null)
            .Add(component => component.Type, SubjectComponentType.Widget)).Markup;

        // Assert
        Assert.Equal(none, hidden);
    }

    [Theory]
    [MemberData(nameof(ValueShowingWidgetTypes))]
    public void WhenAWidgetIsBoundToAStringTheUserMayRead_ThenTheValueRenders(Type widgetType)
    {
        // Arrange
        var widget = _graph.CreateWidget(widgetType, WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text)));

        // Act
        var rendered = _graph.RenderWidget(widget);

        // Assert
        Assert.Contains(Text, rendered.Markup);
    }

    [Fact]
    public void WhenTheLabelOfAWidgetReadsAPropertyTheUserMayNotRead_ThenItsValueNeverRenders()
    {
        // Arrange
        _permissions.CannotRead.Add(_graph.GetProperty(nameof(AccessTestDevice.Text)));
        var tile = (ValueTile)_graph.CreateWidget(typeof(ValueTile), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Flag)));
        tile.Label = "Label {{ subject/" + nameof(AccessTestDevice.Text) + " }}";

        // Act
        var rendered = _graph.RenderWidget(tile);

        // Assert
        Assert.Contains("Label", rendered.Markup);
        Assert.DoesNotContain(Text, rendered.Markup);
    }

    [Fact]
    public void WhenTheOperationsOfASwitchAreHidden_ThenItRendersAsForOperationsThatLeadNowhere()
    {
        // Arrange
        var stop = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Stop));
        _permissions.CannotRead.Add(_graph.GetProperty(nameof(AccessTestDevice.Stop)));

        // Act
        var hidden = RenderSwitch(stop, stop);
        var missing = RenderSwitch(WidgetTestGraph.MissingPath, WidgetTestGraph.MissingPath);

        // Assert
        Assert.Equal(missing, hidden);
    }

    [Theory]
    [InlineData(nameof(AccessTestLock.LockState))]
    [InlineData(nameof(AccessTestLock.DoorState))]
    public void WhenAStateOfADoorLockIsHidden_ThenTheLockRendersAsWithoutThatState(string stateName)
    {
        // Arrange
        var path = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Lock));
        var doorLock = _graph.Device.Lock!;
        var readable = _graph.RenderWidget(_graph.CreateWidget(typeof(DoorLock), path)).Markup;
        _permissions.CannotRead.Add(WidgetTestGraph.GetProperty(doorLock, stateName));
        var hidden = _graph.RenderWidget(_graph.CreateWidget(typeof(DoorLock), path)).Markup;
        _permissions.CannotRead.Clear();

        // Act
        if (stateName == nameof(AccessTestLock.LockState))
        {
            doorLock.LockState = null;
        }
        else
        {
            doorLock.DoorState = null;
        }

        var withoutState = _graph.RenderWidget(_graph.CreateWidget(typeof(DoorLock), path)).Markup;

        // Assert
        Assert.Equal(Normalize(withoutState, path), Normalize(hidden, path));
        Assert.NotEqual(Normalize(readable, path), Normalize(hidden, path));
    }

    [Fact]
    public void WhenACellOfATableIsHidden_ThenItRendersAsACellOfAPropertyTheRowsDoNotHave()
    {
        // Arrange
        const string unknown = "Unknown";
        foreach (var row in _graph.Device.Devices)
        {
            _permissions.CannotRead.Add(WidgetTestGraph.GetProperty(row, nameof(AccessTestRow.Secret)));
        }

        // Act
        var hidden = Normalize(RenderTable(nameof(AccessTestRow.Secret)), nameof(AccessTestRow.Secret));
        var missing = Normalize(RenderTable(unknown), unknown);

        // Assert
        Assert.Equal(missing, hidden);
        Assert.DoesNotContain("secret", hidden);
    }

    [Fact]
    public void WhenTheUserLosesReadAccess_ThenTheWidgetRendersAgainWithoutTheValue()
    {
        // Arrange
        var widget = _graph.CreateWidget(typeof(ValueTile), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text)));
        var rendered = _graph.RenderWidget(widget);
        Assert.Contains(Text, rendered.Markup);

        // Act
        _permissions.CannotRead.Add(_graph.GetProperty(nameof(AccessTestDevice.Text)));
        _permissions.RaiseChanged();

        // Assert
        rendered.WaitForAssertion(() => Assert.DoesNotContain(Text, rendered.Markup));
    }

    // Every subject type that a widget component of HomeBlaze.Components renders, however it is bound.
    private static HashSet<Type> DiscoverWidgetSubjectTypes()
    {
        var types = new HashSet<Type>();
        foreach (var component in typeof(Widget).Assembly.GetTypes())
        {
            foreach (var attribute in component.GetCustomAttributes<SubjectComponentAttribute>(inherit: false))
            {
                if (attribute.ComponentType == SubjectComponentType.Widget)
                {
                    types.Add(attribute.SubjectType);
                }
            }
        }

        return types;
    }

    // A host system needs a logger, which the graph does not offer to the factory of subjects.
    private IInterceptorSubject CreateAnyWidget(Type widgetType)
    {
        if (widgetType != typeof(HostSystem))
        {
            return _graph.CreateWidget(widgetType, WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text)));
        }

        var hostSystem = new HostSystem(NullLogger<HostSystem>.Instance);
        _graph.Device.Widget = hostSystem;
        return hostSystem;
    }

    private static WidgetBinding GetBinding(Type widgetType) =>
        Bindings.TryGetValue(widgetType, out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Bind {widgetType.Name} to a member of {nameof(AccessTestDevice)} in {nameof(Bindings)}, or exclude it with a reason.");

    private string RenderBound(Type widgetType, string path)
    {
        var widget = _graph.CreateWidget(widgetType, path);
        GetBinding(widgetType).Bind?.Invoke(widget, path);
        return Normalize(_graph.RenderWidget(widget).Markup, path);
    }

    private string RenderSwitch(string on, string off)
    {
        var widget = (Switch)_graph.CreateWidget(typeof(Switch), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Flag)));
        widget.On = on;
        widget.Off = off;
        return Normalize(Normalize(_graph.RenderWidget(widget).Markup, on), off);
    }

    private string RenderTable(string columnProperty)
    {
        var table = (SubjectTable)_graph.CreateWidget(typeof(SubjectTable), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Devices)));
        table.Columns[0].Label = "Column";
        table.Columns[0].Property = columnProperty;
        return _graph.RenderWidget(table).Markup;
    }

    // The path shows where a value is missing, and the identifiers MudBlazor and the renderer generate differ from render to render.
    private static string Normalize(string markup, string path) =>
        GeneratedIdentifier().Replace(markup.Replace(path, PathPlaceholder, StringComparison.Ordinal), "$1=\"\"");

    private sealed record WidgetBinding(string Member, Action<IInterceptorSubject, string>? Bind = null);

    [GeneratedRegex("(?<=\\s)(id|for|aria-labelledby|aria-describedby|aria-controls|blazor:[A-Za-z]+)=\"[^\"]*\"")]
    private static partial Regex GeneratedIdentifier();
}
