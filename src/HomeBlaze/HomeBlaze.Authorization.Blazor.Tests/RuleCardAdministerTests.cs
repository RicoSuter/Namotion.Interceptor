using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RuleCardAdministerTests : BunitContext, IAsyncLifetime
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("kitchen");

    private PermissionRule? _changedRule;

    public RuleCardAdministerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
        Services.AddSingleton(_graph.PathResolver);

        Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _graph.Dispose();
    }

    [Fact]
    public void WhenARuleAllowsAdminister_ThenItsAdministerCheckboxIsChecked()
    {
        // Arrange
        var rule = new PermissionRule { Paths = ["/Authorization/Users/**"], Actions = [AuthorizationActions.Administer] };

        // Act
        var component = RenderCard(rule);

        // Assert
        Assert.Equal(
            ["read", "write", "execute", "history", "administer"],
            component.FindComponents<MudCheckBox<bool>>().Select(box => box.Instance.Label ?? string.Empty).ToArray());
        Assert.True(GetCheckBox(component, AuthorizationActions.Administer).Instance.GetState(box => box.Value));
        Assert.False(GetCheckBox(component, PermissionAction.Read).Instance.GetState(box => box.Value));
    }

    [Fact]
    public async Task WhenTheAdministerCheckboxIsToggled_ThenTheRuleGainsAndLosesTheAction()
    {
        // Arrange
        var component = RenderCard(new PermissionRule { Paths = ["/Authorization/AddUser"], Actions = [PermissionAction.Read] });
        var administer = GetCheckBox(component, AuthorizationActions.Administer);

        // Act
        await component.InvokeAsync(() => administer.Instance.ValueChanged.InvokeAsync(true));

        // Assert
        Assert.Equal([PermissionAction.Read, AuthorizationActions.Administer], _changedRule!.Actions.ToArray());
        Assert.Equal(["/Authorization/AddUser"], _changedRule.Paths.ToArray());
        Assert.True(RuleCard.IsValid(_changedRule, _graph.Service));

        // Act
        await component.InvokeAsync(() => GetCheckBox(component, AuthorizationActions.Administer).Instance.ValueChanged.InvokeAsync(false));

        // Assert
        Assert.Equal([PermissionAction.Read], _changedRule.Actions.ToArray());
    }

    private IRenderedComponent<RuleCard> RenderCard(PermissionRule rule) =>
        Render<RuleCard>(parameters => parameters
            .Add(card => card.Rule, rule)
            .Add(card => card.RuleChanged, (PermissionRule changed) => _changedRule = changed));

    private static IRenderedComponent<MudCheckBox<bool>> GetCheckBox(IRenderedComponent<RuleCard> component, PermissionAction action) =>
        component.FindComponents<MudCheckBox<bool>>().Single(box => box.Instance.Label == action.Name);
}
