using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Hosting.Tests;

public class AddKeyedSubjectTests
{
    [Fact]
    public async Task WhenTwoKeyedRegistrations_ThenEachHasItsOwnContextAndBothRun()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddKeyedSubject<CountingHostedSubject>("a", subject => subject.Name = "a");
        builder.Services.AddKeyedSubject<CountingHostedSubject>("b", subject => subject.Name = "b");
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var a = host.Services.GetRequiredKeyedService<CountingHostedSubject>("a");
            var b = host.Services.GetRequiredKeyedService<CountingHostedSubject>("b");

            // Assert
            Assert.Equal("a", a.Name);
            Assert.Equal("b", b.Name);
            Assert.Equal(1, a.StartCount);
            Assert.Equal(1, b.StartCount);
            Assert.NotSame(
                ((IInterceptorSubject)a).Context.TryGetService<HostedServiceHandler>(),
                ((IInterceptorSubject)b).Context.TryGetService<HostedServiceHandler>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTwoKeyedSharedRegistrations_ThenBothRunInTheSharedContext()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddKeyedSubject<CountingHostedSubject>("a", contextResolver: _ => sharedContext);
        builder.Services.AddKeyedSubject<CountingHostedSubject>("b", contextResolver: _ => sharedContext);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var a = host.Services.GetRequiredKeyedService<CountingHostedSubject>("a");
            var b = host.Services.GetRequiredKeyedService<CountingHostedSubject>("b");

            // Assert
            var sharedHandler = sharedContext.TryGetService<HostedServiceHandler>();
            Assert.Same(sharedHandler, ((IInterceptorSubject)a).Context.TryGetService<HostedServiceHandler>());
            Assert.Same(sharedHandler, ((IInterceptorSubject)b).Context.TryGetService<HostedServiceHandler>());
            Assert.Equal(1, a.StartCount);
            Assert.Equal(1, b.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenCallerRegisteredTheKeyedInstanceOutsideAnyContext_ThenItRunsAndStopsWithTheHost()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var subject = new CountingHostedSubject();
        builder.Services.AddKeyedSingleton("a", subject);
        builder.Services.AddKeyedSubject<CountingHostedSubject>("a");
        var host = builder.Build();
        await host.StartAsync();
        var handlerWhileRunning = ((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>();

        // Act
        await host.StopAsync();

        // Assert
        Assert.NotNull(handlerWhileRunning);
        Assert.Same(subject, host.Services.GetRequiredKeyedService<CountingHostedSubject>("a"));
        Assert.Equal(1, subject.StartCount);
        Assert.Equal(1, subject.StopCount);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenKeyIsNull_ThenItBehavesLikeAddSubject()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddKeyedSubject<CountingHostedSubject>(null);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();

            // Assert
            Assert.Equal(1, host.Services.GetRequiredService<CountingHostedSubject>().StartCount);
            Assert.Throws<InvalidOperationException>(() => builder.Services.AddSubject<CountingHostedSubject>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void WhenSameKeyIsRegisteredTwice_ThenThrows()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddKeyedSubject<CountingHostedSubject>("a");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => services.AddKeyedSubject<CountingHostedSubject>("a"));
    }

    [Fact]
    public void WhenUnkeyedAndKeyedRegistrationsOfOneType_ThenBothAreAllowed()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSubject<CountingHostedSubject>();

        // Act
        var exception = Record.Exception(() => services.AddKeyedSubject<CountingHostedSubject>("a"));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void WhenKeyIsAnyKey_ThenThrows()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => services.AddKeyedSubject<CountingHostedSubject>(KeyedService.AnyKey));
    }

    [Fact]
    public async Task WhenTwoKeyedSelfContainedRegistrationsAddTheRegistry_ThenEachConfiguresItsOwnContext()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddKeyedSubject<ContextConfiguratorHostedSubject>("a");
        builder.Services.AddKeyedSubject<ContextConfiguratorHostedSubject>("b");
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var a = host.Services.GetRequiredKeyedService<ContextConfiguratorHostedSubject>("a");
            var b = host.Services.GetRequiredKeyedService<ContextConfiguratorHostedSubject>("b");

            // Assert
            Assert.Equal(1, a.ConfigureContextCount);
            Assert.Equal(1, b.ConfigureContextCount);
            Assert.NotNull(a.TryGetRegisteredSubject());
            Assert.NotNull(b.TryGetRegisteredSubject());
            Assert.NotSame(
                ((IInterceptorSubject)a).Context.TryGetService<HostedServiceHandler>(),
                ((IInterceptorSubject)b).Context.TryGetService<HostedServiceHandler>());
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
