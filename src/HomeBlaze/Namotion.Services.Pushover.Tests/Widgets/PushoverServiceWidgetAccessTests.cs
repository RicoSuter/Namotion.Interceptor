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
/// The widget leaves out the targets the viewer may not see.
/// </summary>
public sealed class PushoverServiceWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public PushoverServiceWidgetAccessTests()
    {
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public void WhenATargetIsHidden_ThenTheWidgetDoesNotNameIt()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService("Alice", "Bob");
        ((IInterceptorSubject)service).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        _permissions.CannotShow.Add(service.Targets["Bob"]);

        // Act
        var rendered = Render<PushoverServiceWidget>(parameters => parameters.Add(widget => widget.Subject, service));

        // Assert
        Assert.Contains("Alice", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", rendered.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenEveryTargetIsHidden_ThenTheWidgetShowsNoTargets()
    {
        // Arrange
        var (service, _) = PushoverTestFactory.CreateService("Alice");
        ((IInterceptorSubject)service).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        _permissions.CannotShow.Add(service.Targets["Alice"]);

        // Act
        var rendered = Render<PushoverServiceWidget>(parameters => parameters.Add(widget => widget.Subject, service));

        // Assert
        Assert.DoesNotContain("Alice", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("No targets configured", rendered.Markup, StringComparison.Ordinal);
    }
}
