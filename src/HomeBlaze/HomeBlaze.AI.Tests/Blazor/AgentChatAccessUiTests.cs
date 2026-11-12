using Bunit;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public sealed class AgentChatAccessUiTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();
    private readonly FakeAgent _agent;

    public AgentChatAccessUiTests()
    {
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        _agent = new FakeAgent(services) { Roles = [BuiltInRoles.OperatorName] };

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public void WhenThePersonLacksAnAgentRole_ThenTheChatInputIsReplacedByTheReason()
    {
        // Arrange
        _permissions.HeldRoles.Add(BuiltInRoles.ReaderName);

        // Act
        var tab = Render<AgentChatTab>(parameters => parameters.Add(component => component.Agent, _agent));

        // Assert
        Assert.Empty(tab.FindAll("textarea"));
        Assert.Empty(tab.FindAll("[data-testid='agent-chat-send']"));
        Assert.Contains("Missing: operator", tab.Markup);
    }

    [Fact]
    public void WhenThePersonHoldsTheAgentRoles_ThenTheChatInputIsShown()
    {
        // Arrange
        _permissions.HeldRoles.Add(BuiltInRoles.OperatorName);

        // Act
        var tab = Render<AgentChatTab>(parameters => parameters.Add(component => component.Agent, _agent));

        // Assert
        Assert.NotEmpty(tab.FindAll("textarea"));
        Assert.DoesNotContain("Missing:", tab.Markup);
    }

    [Fact]
    public void WhenTheRolesOfThePersonChange_ThenTheChatInputFollows()
    {
        // Arrange
        _permissions.HeldRoles.Add(BuiltInRoles.OperatorName);
        var tab = Render<AgentChatTab>(parameters => parameters.Add(component => component.Agent, _agent));

        // Act
        _permissions.HeldRoles.Clear();
        _permissions.RaiseChanged();

        // Assert
        tab.WaitForAssertion(() => Assert.Empty(tab.FindAll("textarea")));
        Assert.Contains("Missing: operator", tab.Markup);
    }

    [Fact]
    public async Task WhenTheTabIsDisposed_ThenItStopsListeningToPermissionChanges()
    {
        // Arrange
        Render<AgentChatTab>(parameters => parameters.Add(component => component.Agent, _agent));
        Assert.Equal(1, _permissions.ChangedSubscriberCount);

        // Act
        await DisposeComponentsAsync();

        // Assert
        Assert.Equal(0, _permissions.ChangedSubscriberCount);
    }

    [Fact]
    public void WhenSendIsClickedAfterTheRolesWereLostWithoutANotification_ThenNoTurnStarts()
    {
        // Arrange
        _permissions.HeldRoles.Add(BuiltInRoles.OperatorName);
        var tab = Render<AgentChatTab>(parameters => parameters.Add(component => component.Agent, _agent));
        tab.Find("textarea").Input("hello");
        _permissions.HeldRoles.Clear();

        // Act
        tab.Find("[data-testid='agent-chat-send']").Click();

        // Assert
        Assert.Empty(_agent.Requests);
        Assert.Empty(_agent.Timeline.Entries);
        Assert.Contains("Missing: operator", tab.Markup);
        Assert.Empty(tab.FindAll("textarea"));
    }

    [Fact]
    public void WhenThePersonLacksAnAgentRole_ThenRunNowAndNewConversationAreDisabled()
    {
        // Arrange
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        _permissions.HeldRoles.Add(BuiltInRoles.ReaderName);

        // Act
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, _agent));

        // Assert
        Assert.True(page.Find("[data-testid='agent-run-now']").HasAttribute("disabled"));
        Assert.True(page.Find("[data-testid='agent-new-conversation']").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenNewConversationIsClickedAfterTheRolesWereLostWithoutANotification_ThenNothingIsAskedOrReset()
    {
        // Arrange
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        _permissions.HeldRoles.Add(BuiltInRoles.OperatorName);
        var agent = CreateAgentWithOperations();
        var dialogs = Render<MudDialogProvider>();
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, agent));
        var button = page.Find("[data-testid='agent-new-conversation']");
        Assert.False(button.HasAttribute("disabled"));
        _permissions.HeldRoles.Clear();

        // Act
        button.Click();

        // Assert
        Assert.Empty(dialogs.FindAll(".mud-dialog"));
        Assert.Equal(0, agent.ResetCount);
    }

    [Fact]
    public async Task WhenThePersonLacksAnAgentRole_ThenNoTimelineActivityOrMemoryEntryShows()
    {
        // Arrange
        await using var test = await SeedAsync();
        _permissions.HeldRoles.Add(BuiltInRoles.ReaderName);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();

        // Act
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, test.Agent));
        var chat = page.Markup;
        SelectTab(page, "Activity");
        var activity = page.Markup;
        SelectTab(page, "Memory");
        var memory = page.Markup;

        // Assert
        Assert.DoesNotContain(SecretText, chat);
        Assert.Empty(page.FindAll("[data-testid='agent-timeline-row']"));
        Assert.Contains("Missing: operator", chat);
        Assert.DoesNotContain("agent-runs", activity);
        Assert.Contains("Missing: operator", activity);
        Assert.DoesNotContain("secret/key", memory);
        Assert.Contains("Missing: operator", memory);
    }

    [Fact]
    public async Task WhenThePersonHoldsTheAgentRoles_ThenTheTimelineActivityAndMemoryEntriesShow()
    {
        // Arrange
        await using var test = await SeedAsync();
        _permissions.HeldRoles.Add(BuiltInRoles.OperatorName);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();

        // Act
        var page = Render<AgentPage>(parameters => parameters.Add(component => component.Subject, test.Agent));
        var chat = page.Markup;
        SelectTab(page, "Activity");
        var activity = page.Markup;
        SelectTab(page, "Memory");
        var memory = page.Markup;

        // Assert
        Assert.Contains(SecretText, chat);
        Assert.Contains("agent-runs", activity);
        Assert.DoesNotContain("Missing: operator", activity);
        Assert.Contains("secret/key", memory);
    }

    [Fact]
    public void WhenTheRefusalClears_ThenTheEnterListenerIsAttachedAgain()
    {
        // Arrange
        var module = JSInterop.SetupModule("./_content/HomeBlaze.AI.Blazor/AgentChatTab.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        module.SetupModule("attachEnterToSend", _ => true).Mode = JSRuntimeMode.Loose;
        JSInterop.Mode = JSRuntimeMode.Loose;
        var tab = Render<AgentChatTab>(parameters => parameters.Add(component => component.Agent, _agent));
        var attachCallsWhileRefused = module.Invocations["attachEnterToSend"].Count;

        // Act
        _permissions.HeldRoles.Add(BuiltInRoles.OperatorName);
        _permissions.RaiseChanged();
        tab.WaitForAssertion(() => Assert.NotEmpty(tab.FindAll("textarea")));

        // Assert
        Assert.Equal(0, attachCallsWhileRefused);
        tab.WaitForAssertion(() => Assert.Single(module.Invocations["attachEnterToSend"]));
    }

    private const string SecretText = "the freezer code is 1234";

    private async Task<AgentTestContext<FakeAgent>> SeedAsync()
    {
        var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services), agent => agent.Roles = [BuiltInRoles.OperatorName]);
        await test.Agent.SendMessageAsync(SecretText, CancellationToken.None);
        await test.Agent.Memory.UpdateAsync([AgentMemoryChange.Set("secret/key", "secret value")], CancellationToken.None);
        return test;
    }

    // An agent attached to a graph that registers its operations, which the buttons of the page invoke.
    private static FakeAgent CreateAgentWithOperations()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer)
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer);
        var root = new AgentTestRoot(context);
        var agent = new FakeAgent(AgentTestContext.CreateServices(root)) { Roles = [BuiltInRoles.OperatorName] };
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };
        return agent;
    }

    private static void SelectTab(IRenderedComponent<AgentPage> page, string text) =>
        page.FindAll(".mud-tab").First(tab => tab.TextContent.Contains(text, StringComparison.Ordinal)).Click();
}
