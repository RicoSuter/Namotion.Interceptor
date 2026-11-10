using Bunit;
using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

public sealed class ControlAccessTests : BunitContext, IAsyncLifetime
{
    private const string DeniedTooltip = "Your roles do not allow this.";

    private readonly TestUserPermissions _permissions = new();
    private readonly WidgetTestGraph _graph;

    public ControlAccessTests()
    {
        _graph = WidgetTestGraph.Create(this, "Text");
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public static TheoryData<Type, string> WritingControls() => new()
    {
        { typeof(Switch), nameof(AccessTestDevice.Flag) },
        { typeof(Slider), nameof(AccessTestDevice.Level) },
        { typeof(Select), nameof(AccessTestDevice.Mode) },
        { typeof(Control), nameof(AccessTestDevice.Flag) },
    };

    public static TheoryData<Type, string> ConfiguringControls() => new()
    {
        { typeof(Switch), nameof(AccessTestDevice.ConfiguredFlag) },
        { typeof(Slider), nameof(AccessTestDevice.ConfiguredLevel) },
        { typeof(Select), nameof(AccessTestDevice.ConfiguredMode) },
        { typeof(Control), nameof(AccessTestDevice.ConfiguredFlag) },
    };

    public static TheoryData<Type> InvokingControls() => [typeof(OperationButton), typeof(Control)];

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _graph.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(WritingControls))]
    public void WhenTheUserMayWrite_ThenTheControlIsEnabled(Type widgetType, string member)
    {
        // Act
        var rendered = Render(widgetType, member);

        // Assert
        Assert.Empty(rendered.FindAll("[disabled]"));
        Assert.Empty(rendered.FindAll($"[title='{DeniedTooltip}']"));
    }

    [Theory]
    [MemberData(nameof(WritingControls))]
    public void WhenTheUserMayNotWrite_ThenTheControlIsDisabledWithTheReason(Type widgetType, string member)
    {
        // Arrange
        _permissions.CannotWrite.Add(_graph.GetProperty(member));

        // Act
        var rendered = Render(widgetType, member);

        // Assert
        AssertDisabledWithReason(rendered);
    }

    [Theory]
    [MemberData(nameof(ConfiguringControls))]
    public void WhenTheUserMayAdministerAConfigurationProperty_ThenTheControlIsEnabledWhateverWriteAllows(Type widgetType, string member)
    {
        // Arrange
        _permissions.CannotWrite.Add(_graph.GetProperty(member));

        // Act
        var rendered = Render(widgetType, member);

        // Assert
        Assert.Empty(rendered.FindAll("[disabled]"));
        Assert.Empty(rendered.FindAll($"[title='{DeniedTooltip}']"));
    }

    [Theory]
    [MemberData(nameof(ConfiguringControls))]
    public void WhenTheUserMayNotAdministerAConfigurationProperty_ThenTheControlIsDisabledWithTheReason(Type widgetType, string member)
    {
        // Arrange
        _permissions.CannotAdminister.Add(_graph.GetProperty(member));

        // Act
        var rendered = Render(widgetType, member);

        // Assert
        AssertDisabledWithReason(rendered);
    }

    [Theory]
    [MemberData(nameof(InvokingControls))]
    public void WhenTheUserMayInvoke_ThenTheButtonIsEnabled(Type widgetType)
    {
        // Act
        var rendered = Render(widgetType, nameof(AccessTestDevice.Stop));

        // Assert
        Assert.Empty(rendered.FindAll("[disabled]"));
        Assert.Empty(rendered.FindAll($"[title='{DeniedTooltip}']"));
    }

    [Theory]
    [MemberData(nameof(InvokingControls))]
    public void WhenTheUserMayNotInvoke_ThenTheButtonIsDisabledWithTheReason(Type widgetType)
    {
        // Arrange
        _permissions.CannotInvoke.Add(_graph.GetProperty(nameof(AccessTestDevice.Stop)));

        // Act
        var rendered = Render(widgetType, nameof(AccessTestDevice.Stop));

        // Assert
        AssertDisabledWithReason(rendered);
    }

    [Theory]
    [MemberData(nameof(InvokingControls))]
    public void WhenTheOperationIsDisabledWithAReason_ThenTheButtonIsDisabledWithTheReason(Type widgetType)
    {
        // Arrange
        var stop = _graph.GetProperty(nameof(AccessTestDevice.Stop));
        stop.AddAttribute<bool>(KnownAttributes.IsEnabled, _ => false);
        stop.AddAttribute<string?>(KnownAttributes.DisabledReason, _ => "Not while the device is offline.");

        // Act
        var rendered = Render(widgetType, nameof(AccessTestDevice.Stop));

        // Assert
        var button = Assert.Single(rendered.FindAll("[title='Not while the device is offline.']"));
        Assert.True(button.HasAttribute("disabled") || button.QuerySelectorAll("[disabled]").Length > 0, rendered.Markup);
    }

    [Fact]
    public void WhenTheUserMayNotInvokeTheOperationOfASwitch_ThenTheSwitchIsDisabledWithTheReason()
    {
        // Arrange
        var stop = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Stop));
        var widget = (Switch)_graph.CreateWidget(typeof(Switch), WidgetTestGraph.PathOf(nameof(AccessTestDevice.Flag)));
        widget.On = stop;
        widget.Off = stop;
        _permissions.CannotInvoke.Add(_graph.GetProperty(nameof(AccessTestDevice.Stop)));

        // Act
        var rendered = _graph.RenderWidget(widget);

        // Assert
        AssertDisabledWithReason(rendered);
    }

    private IRenderedComponent<SubjectComponent> Render(Type widgetType, string member)
    {
        var widget = _graph.CreateWidget(widgetType, WidgetTestGraph.PathOf(member));
        return _graph.RenderWidget(widget);
    }

    private static void AssertDisabledWithReason(IRenderedComponent<SubjectComponent> rendered)
    {
        var reason = Assert.Single(rendered.FindAll($"[title='{DeniedTooltip}']"));
        Assert.True(reason.HasAttribute("disabled") || reason.QuerySelectorAll("[disabled]").Length > 0, rendered.Markup);
    }
}
