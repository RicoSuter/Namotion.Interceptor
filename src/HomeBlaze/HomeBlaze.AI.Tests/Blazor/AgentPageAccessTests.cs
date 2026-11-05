using Bunit;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public sealed class AgentPageAccessTests : BunitContext, IAsyncLifetime
{
    private const string DeniedTooltip = "Your roles do not allow this.";

    private readonly TestUserPermissions _permissions = new() { HeldRoles = [BuiltInRoles.EverythingName] };
    private readonly FakeAgent _agent;

    public AgentPageAccessTests()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
        var root = new AgentTestRoot(context);
        var services = AgentTestContext.CreateServices(root);
        _agent = new FakeAgent(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = _agent };

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton(services.AuthorizationService);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public void WhenTheUserMayRunTheAgent_ThenRunNowIsEnabled()
    {
        // Act
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, _agent));

        // Assert
        Assert.False(page.Find("[data-testid='agent-run-now']").HasAttribute("disabled"));
        Assert.False(page.Find("[data-testid='agent-run-now-icon']").HasAttribute("disabled"));
        Assert.False(page.Find("[data-testid='agent-new-conversation']").HasAttribute("disabled"));
        Assert.Empty(page.FindAll($"[title='{DeniedTooltip}']"));
    }

    [Fact]
    public void WhenTheUserMayNotAdministerRunNow_ThenRunNowIsDisabledWithTheReason()
    {
        // Arrange: Run Now requires administration.
        _permissions.CannotAdminister.Add(_agent.TryGetRegisteredProperty("RunNow")!);

        // Act
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, _agent));

        // Assert
        var runNow = page.Find("[data-testid='agent-run-now']");
        Assert.True(runNow.HasAttribute("disabled"));
        Assert.Equal(DeniedTooltip, runNow.GetAttribute("title"));
        Assert.True(page.Find("[data-testid='agent-run-now-icon']").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenTheUserMayNotStartANewConversation_ThenItIsDisabledWithTheReason()
    {
        // Arrange
        _permissions.CannotInvoke.Add(_agent.TryGetRegisteredProperty("NewConversation")!);

        // Act
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, _agent));

        // Assert
        var newConversation = page.Find("[data-testid='agent-new-conversation']");
        Assert.True(newConversation.HasAttribute("disabled"));
        Assert.Equal(DeniedTooltip, newConversation.GetAttribute("title"));
        Assert.True(page.Find("[data-testid='agent-new-conversation-icon']").HasAttribute("disabled"));
        Assert.False(page.Find("[data-testid='agent-run-now']").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenTheRolesOfTheUserChange_ThenRunNowFollows()
    {
        // Arrange
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, _agent));

        // Act
        _permissions.CannotAdminister.Add(_agent.TryGetRegisteredProperty("RunNow")!);
        _permissions.RaiseChanged();

        // Assert
        page.WaitForAssertion(() => Assert.True(page.Find("[data-testid='agent-run-now']").HasAttribute("disabled")));
    }
}
