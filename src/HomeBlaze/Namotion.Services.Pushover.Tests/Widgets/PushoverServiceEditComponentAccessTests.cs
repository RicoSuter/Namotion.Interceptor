using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Services.Pushover.HomeBlaze;
using Namotion.Services.Pushover.Tests.Testing;
using Xunit;

namespace Namotion.Services.Pushover.Tests.Widgets;

/// <summary>
/// The edit component neither lists nor removes the targets the viewer may not see, and edits no target hidden since it opened.
/// </summary>
public sealed class PushoverServiceEditComponentAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public PushoverServiceEditComponentAccessTests()
    {
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public async Task WhenATargetIsHidden_ThenTheComponentDoesNotListItAndASaveKeepsIt()
    {
        // Arrange
        var service = CreateService("Alice", "Bob");
        var bob = service.Targets["Bob"];
        _permissions.CannotShow.Add(bob);
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));

        // Act
        await rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Contains("Alice", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", rendered.Markup, StringComparison.Ordinal);
        Assert.Equal(["Alice", "Bob"], service.Targets.Keys.Order());
        Assert.Same(bob, service.Targets["Bob"]);
    }

    [Fact]
    public async Task WhenTheViewerRemovesEveryTargetTheySee_ThenTheHiddenTargetsKeepTheServiceValid()
    {
        // Arrange
        var service = CreateService("Alice", "Bob");
        _permissions.CannotShow.Add(service.Targets["Bob"]);
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));

        // Act
        await rendered.InvokeAsync(() => rendered.FindAll("button").Single(button => button.TextContent.Contains("Remove")).Click());
        await rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(rendered.Instance.IsValid);
        Assert.Equal(["Bob"], service.Targets.Keys);
    }

    [Fact]
    public async Task WhenATargetIsHidden_ThenASaveKeepsTheOrderOfTheTargets()
    {
        // Arrange
        var service = CreateService("Alice", "Bob", "Carol");
        _permissions.CannotShow.Add(service.Targets["Bob"]);
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));

        // Act
        await rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["Alice", "Bob", "Carol"], service.Targets.Keys);
    }

    [Fact]
    public async Task WhenAListedTargetIsHiddenBeforeTheSave_ThenTheSaveIsRefusedAndChangesNothing()
    {
        // Arrange
        var service = CreateService("Alice", "Bob");
        var targets = service.Targets;
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));
        await rendered.InvokeAsync(() => rendered.FindAll("button").Last(button => button.TextContent.Contains("Remove")).Click());
        _permissions.CannotShow.Add(service.Targets["Bob"]);

        // Act
        var exception = await Record.ExceptionAsync(() => rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Same(targets, service.Targets);
        Assert.Equal(["Alice", "Bob"], service.Targets.Keys);
    }

    [Fact]
    public async Task WhenAListedKeyNamesAnotherTargetBeforeTheSave_ThenTheSaveIsRefusedAndChangesNothing()
    {
        // Arrange
        var service = CreateService("alice", "bob");
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));
        var other = new PushoverTarget { Name = "Other", UserKey = "user-other" };
        var targets = new Dictionary<string, PushoverTarget>(service.Targets) { ["alice"] = other };
        service.Targets = targets;

        // Act
        var exception = await Record.ExceptionAsync(() => rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("changed elsewhere", exception.Message, StringComparison.Ordinal);
        Assert.Same(targets, service.Targets);
        Assert.Equal("Other", other.Name);
    }

    [Fact]
    public async Task WhenANewTargetWasSaved_ThenTheNextSaveKeepsItAsListed()
    {
        // Arrange
        var service = CreateService("alice");
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));
        await rendered.InvokeAsync(() => rendered.FindAll("button").Single(button => button.TextContent.Contains("Add Target")).Click());
        var fields = rendered.FindAll("input");
        await rendered.InvokeAsync(() => fields[4].Input("bob"));
        await rendered.InvokeAsync(() => fields[5].Input("user-bob"));
        await rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None));
        var bob = service.Targets["bob"];

        // Act
        var exception = await Record.ExceptionAsync(() => rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.Null(exception);
        Assert.Equal(["alice", "bob"], service.Targets.Keys);
        Assert.Same(bob, service.Targets["bob"]);
    }

    [Fact]
    public async Task WhenAListedTargetWasRemovedElsewhereAndANewTargetGetsItsName_ThenTheSaveKeepsBothUnderDifferentKeys()
    {
        // Arrange
        var service = CreateService("alice");
        var rendered = Render<PushoverServiceEditComponent>(parameters => parameters.Add(component => component.Subject, service));
        service.Targets = new Dictionary<string, PushoverTarget>();
        await rendered.InvokeAsync(() => rendered.FindAll("button").Single(button => button.TextContent.Contains("Add Target")).Click());
        var fields = rendered.FindAll("input");
        await rendered.InvokeAsync(() => fields[3].Input("user-alice"));
        await rendered.InvokeAsync(() => fields[4].Input("alice"));
        await rendered.InvokeAsync(() => fields[5].Input("user-new"));

        // Act
        await rendered.InvokeAsync(() => rendered.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["alice", "alice-2"], service.Targets.Keys);
        Assert.Equal(["user-alice", "user-new"], service.Targets.Values.Select(target => target.UserKey));
    }

    private static PushoverService CreateService(params string[] targetKeys)
    {
        var (service, _) = PushoverTestFactory.CreateService(targetKeys);
        ((IInterceptorSubject)service).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        return service;
    }
}
