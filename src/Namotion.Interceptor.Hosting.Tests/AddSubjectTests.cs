using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class AddSubjectTests
{
    [Fact]
    public async Task WhenSubjectHasGeneratedContextConstructor_ThenItStartsExactlyOnce()
    {
        // Arrange - the context attach starts it and AddHostedService started it again: two starts,
        // a second execute task and an orphaned token source.
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<PersonWithBackgroundService>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<PersonWithBackgroundService>();

            // Assert
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectHasDependencyInjectedConstructor_ThenItIsStillAttachedToTheContext()
    {
        // Arrange - the generator emits the (IInterceptorSubjectContext) constructor only when the
        // first declared constructor is parameterless, so this shape used to get no context at all.
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert - registry membership is the observable for "attached to the context"
            var registry = context.GetService<ISubjectRegistry>();
            Assert.Contains(registry.KnownSubjects, known => ReferenceEquals(known.Key, subject));
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenThereIsNoHostingHandler_ThenTheActivationStartsTheSubjectItself()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext.Create().WithContextInheritance();
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectIsCalledTwice_ThenOnlyOneActivationIsRegistered()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();
        builder.Services.AddSubject<SubjectWithDependencies>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectIsRegisteredBeforeWithHostedServices_ThenStartupDoesNotHang()
    {
        // Arrange - the activation awaits a transition gated on the handler having started. Without
        // EnsureStartedAsync opening the gate, host startup would deadlock on registration order.
        var builder = Host.CreateApplicationBuilder();

        var contextHolder = new IInterceptorSubjectContext[1];
        builder.Services.AddSingleton(_ => contextHolder[0]!);
        builder.Services.AddSubject<SubjectWithDependencies>();

        contextHolder[0] = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithRegistry()
            .WithHostedServices(builder.Services);

        var host = builder.Build();

        // Act
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            // Assert
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IInterceptorSubjectContext CreateContext(HostApplicationBuilder builder)
        => InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithRegistry()
            .WithHostedServices(builder.Services);
}
