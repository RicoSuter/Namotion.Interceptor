using Bunit;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services.Lifecycle;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public sealed class AgentEditComponentTests : BunitContext, IAsyncLifetime
{
    private readonly AgentTestRoot _root;
    private readonly AgentServices _services;
    private readonly TestUserPermissions _permissions = new();

    public AgentEditComponentTests()
    {
        // The graph has two custom roles whose names differ only in case, so neither can be used.
        _root = CreateRootWithUnusableRoles();
        _services = AgentTestContext.CreateServices(_root);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_services.ToolSource);
        Services.AddSingleton(_services.AuthorizationService);
        Services.AddSingleton<IUserPermissions>(_permissions);
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
        var agent = new FakeAgent(_services) { Roles = [BuiltInRoles.ReaderName, "ghost"] };

        // Act
        var component = RenderEditComponent(agent);

        // Assert
        var chip = component.Find("[data-testid='agent-edit-role-ghost']");
        Assert.Contains("mud-chip-color-error", chip.ClassName);
        Assert.DoesNotContain("mud-chip-color-error", component.Find("[data-testid='agent-edit-role-reader']").ClassName);
        Assert.Single(component.FindComponents<MudTooltip>(), tooltip => tooltip.Instance.Text == "Unknown role");
    }

    [Fact]
    public void WhenARoleExistsButCannotBeUsed_ThenItIsShownAsAnErrorChipWithTheReason()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Roles = ["kitchen/lights"] };

        // Act
        var component = RenderEditComponent(agent);

        // Assert
        Assert.Contains("mud-chip-color-error", component.Find("[data-testid='agent-edit-role-kitchen/lights']").ClassName);
        var tooltip = Assert.Single(component.FindComponents<MudTooltip>());
        Assert.Equal(NameRules.GetNameError("kitchen/lights", "role name"), tooltip.Instance.Text);
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
        await component.Find("[data-testid='agent-edit-role-operator'] .mud-chip-close-button").ClickAsync(new MouseEventArgs());
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.False(isValid);
        Assert.False(component.Instance.IsValid);
        Assert.True(component.Instance.IsDirty);
        Assert.Contains("Select at least one role", component.Find("[data-testid='agent-edit-roles']").TextContent);
        Assert.Equal([BuiltInRoles.OperatorName], agent.Roles);
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerASettingOfTheAgent_ThenTheFormIsDisabledAndSavingWritesNothing()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Name = "Watcher" };
        var component = RenderEditComponent(agent);
        _permissions.CannotAdminister.Add(agent.TryGetRegisteredSubject()!.TryGetProperty(nameof(LlmAgentBase.Instructions))!);
        _permissions.RaiseChanged();
        var nameField = component.FindComponents<MudTextField<string>>()[0];

        // Act
        await component.InvokeAsync(() => nameField.Instance.ValueChanged.InvokeAsync("Changed"));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.Equal("Your roles do not allow this change.", exception.Message);
        Assert.Equal("Watcher", agent.Name);
        Assert.True(component.FindComponent<MudForm>().Instance.Disabled);
    }

    [Fact]
    public void WhenTheViewerMayAdministerTheSettingsOfTheAgent_ThenTheFormIsEnabled()
    {
        // Arrange
        var agent = new FakeAgent(_services);

        // Act
        var component = RenderEditComponent(agent);

        // Assert
        Assert.False(component.FindComponent<MudForm>().Instance.Disabled);
    }

    private static AgentTestRoot CreateRootWithUnusableRoles()
    {
        // The attribute initializer marks the configuration members, which the viewer administers.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);
        // A path character is not allowed in a role name, so the role cannot be used.
        var authorization = new Authorization.Authorization();
        authorization.ReplaceRoles([new AuthorizationRole { Name = "kitchen/lights" }]);
        return new AgentTestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization }
        };
    }

    // The agent is put into the graph, as only a registered agent has settings the viewer may administer.
    private IRenderedComponent<AgentEditComponent> RenderEditComponent(LlmAgentBase agent)
    {
        _root.Children = new Dictionary<string, IInterceptorSubject>(_root.Children) { ["Agent"] = agent };
        return Render<AgentEditComponent>(parameters => parameters.Add(component => component.Subject, agent));
    }
}
