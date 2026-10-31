using Bunit;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public sealed class AgentEditComponentTests : BunitContext, IAsyncLifetime
{
    // The graph has two custom roles whose names differ only in case, so neither can be used.
    private readonly AgentServices _services = AgentTestContext.CreateServices(CreateRootWithUnusableRoles());

    public AgentEditComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_services.ToolSource);
        Services.AddSingleton(_services.AuthorizationService);
        Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public void WhenTheAgentHasRoles_ThenTheyAreShownAsChips()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Roles = [BuiltInRoles.ReaderName, BuiltInRoles.EverythingName] };

        // Act
        var component = RenderEditComponent(agent);

        // Assert
        Assert.Equal(
            [BuiltInRoles.ReaderName, BuiltInRoles.EverythingName],
            component.FindAll("[data-testid^='agent-edit-role-']").Select(chip => chip.TextContent.Trim()));
    }

    [Fact]
    public async Task WhenARoleIsSelectedAndSaved_ThenTheAgentHasIt()
    {
        // Arrange
        var agent = new FakeAgent(_services);
        var component = RenderEditComponent(agent);
        var select = component.FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync([BuiltInRoles.OperatorName, BuiltInRoles.ReaderName]));
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(component.Instance.IsValid);
        Assert.Equal([BuiltInRoles.OperatorName, BuiltInRoles.ReaderName], agent.Roles);
    }

    [Fact]
    public void WhenARoleIsUnknown_ThenItIsShownAsAnErrorChip()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Roles = [BuiltInRoles.ReaderName, "Ghost"] };

        // Act
        var component = RenderEditComponent(agent);

        // Assert
        var chip = component.Find("[data-testid='agent-edit-role-Ghost']");
        Assert.Contains("mud-chip-color-error", chip.ClassName);
        Assert.DoesNotContain("mud-chip-color-error", component.Find("[data-testid='agent-edit-role-Reader']").ClassName);
        Assert.Single(component.FindComponents<MudTooltip>(), tooltip => tooltip.Instance.Text == "Unknown role");
    }

    [Fact]
    public void WhenARoleExistsButCannotBeUsed_ThenItIsShownAsAnErrorChipWithTheReason()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Roles = ["Twin"] };

        // Act
        var component = RenderEditComponent(agent);

        // Assert
        Assert.Contains("mud-chip-color-error", component.Find("[data-testid='agent-edit-role-Twin']").ClassName);
        var tooltip = Assert.Single(component.FindComponents<MudTooltip>());
        Assert.Equal("The name 'Twin' is used by more than one role, ignoring case.", tooltip.Instance.Text);
    }

    [Fact]
    public async Task WhenTheLastRoleIsRemoved_ThenSavingIsPrevented()
    {
        // Arrange
        var agent = new FakeAgent(_services);
        var component = RenderEditComponent(agent);
        bool? isValid = null;
        component.Instance.IsValidChanged += value => isValid = value;

        // Act
        await component.Find("[data-testid='agent-edit-role-Operator'] .mud-chip-close-button").ClickAsync(new MouseEventArgs());
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.False(isValid);
        Assert.False(component.Instance.IsValid);
        Assert.True(component.Instance.IsDirty);
        Assert.Contains("Select at least one role", component.Find("[data-testid='agent-edit-roles']").TextContent);
        Assert.Equal([BuiltInRoles.OperatorName], agent.Roles);
    }

    private static AgentTestRoot CreateRootWithUnusableRoles()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        return new AgentTestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Authorization"] = new Authorization.Authorization
                {
                    Roles = new Dictionary<string, Role> { ["Twin"] = new(), ["TWIN"] = new() }
                }
            }
        };
    }

    private IRenderedComponent<AgentEditComponent> RenderEditComponent(LlmAgentBase agent) =>
        Render<AgentEditComponent>(parameters => parameters.Add(component => component.Subject, agent));
}
