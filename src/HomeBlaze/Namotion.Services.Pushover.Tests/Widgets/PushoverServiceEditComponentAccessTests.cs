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
/// The edit component neither lists nor removes the targets the viewer may not see.
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

    private static PushoverService CreateService(params string[] targetKeys)
    {
        var (service, _) = PushoverTestFactory.CreateService(targetKeys);
        ((IInterceptorSubject)service).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        return service;
    }
}
