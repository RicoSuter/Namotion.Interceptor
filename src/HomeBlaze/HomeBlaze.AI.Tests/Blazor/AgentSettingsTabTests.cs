using Bunit;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public sealed class AgentSettingsTabTests : BunitContext, IAsyncLifetime
{
    private readonly AgentServices _services = AgentTestContext.CreateServices(new AgentTestRoot());

    public AgentSettingsTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_services.AuthorizationService);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public void WhenTheAgentHasRoles_ThenTheSettingsShowThem()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Roles = [BuiltInRoles.ReaderName, BuiltInRoles.OperatorName] };

        // Act
        var component = Render<AgentSettingsTab>(parameters => parameters.Add(tab => tab.Agent, agent));

        // Assert
        var roles = component.Find("[data-testid='agent-settings-roles']");
        Assert.Contains(BuiltInRoles.ReaderName, roles.TextContent);
        Assert.Contains(BuiltInRoles.OperatorName, roles.TextContent);
        Assert.Empty(component.FindAll("[data-testid='agent-settings-roles-error']"));
    }

    [Fact]
    public void WhenTheRolesCannotBeUsed_ThenTheSettingsShowWhy()
    {
        // Arrange
        var agent = new FakeAgent(_services) { Roles = [BuiltInRoles.ReaderName, "Ghost"] };

        // Act
        var component = Render<AgentSettingsTab>(parameters => parameters.Add(tab => tab.Agent, agent));

        // Assert
        Assert.Contains("Ghost", component.Find("[data-testid='agent-settings-roles']").TextContent);
        var error = component.Find("[data-testid='agent-settings-roles-error']").TextContent;
        Assert.Contains("Ghost", error);
        Assert.Contains("denied everything", error);
    }
}
