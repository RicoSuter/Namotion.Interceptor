using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class AddSubjectTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenSubjectHasGeneratedContextConstructor_ThenItStartsExactlyOnce()
    {
        // Arrange - the context attach starts it and AddHostedService started it again: two starts,
        // a second execute task and an orphaned token source.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
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
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
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
        var builder = HostingTestHost.CreateBuilder();
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
    public void WhenTheCallerAlreadyRegisteredTheType_ThenAddSubjectDoesNotThrow()
    {
        // Arrange - a caller who built the subject themselves and registers AddSubject to start it is a
        // different case from calling AddSubject twice, and the guard must not catch it.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(new SubjectWithDependencies(NullLogger<SubjectWithDependencies>.Instance));

        // Act
        var exception = Record.Exception(() => builder.Services.AddSubject<SubjectWithDependencies>());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void WhenAddSubjectIsCalledTwiceForOneType_ThenItThrows()
    {
        // Arrange - the second call cannot take effect: the singleton registration is a TryAdd, so its
        // configure and contextResolver would be dropped while reading as a working registration.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.Services.AddSubject<SubjectWithDependencies>());

        Assert.Contains(nameof(SubjectWithDependencies), exception.Message);
    }

    [Fact]
    public async Task WhenAddSubjectIsRegisteredBeforeWithHostedServices_ThenStartupDoesNotHang()
    {
        // Arrange - the activation awaits a transition gated on the handler having started. Without
        // EnsureStarted opening the gate, host startup would deadlock on registration order.
        var builder = HostingTestHost.CreateBuilder();

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

    [Fact]
    public async Task WhenTheSubjectHasNoContextConstructor_ThenConfigureCompletesBeforeTheSubjectCanStart()
    {
        // Arrange - the attach is what makes the handler append a start, so a configure that ran
        // after it would race that start with nothing between them but the handler's start delay.
        // Holding configure open makes the ordering observable instead of timing dependent.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
        builder.Services.AddSingleton(context);

        using var configureEntered = new ManualResetEventSlim();
        using var releaseConfigure = new ManualResetEventSlim();
        SubjectWithDependencies? configuredSubject = null;

        builder.Services.AddSubject<SubjectWithDependencies>(subject =>
        {
            configuredSubject = subject;
            configureEntered.Set();
            releaseConfigure.Wait(WaitTimeout);
            subject.Name = "configured";
        });

        var host = builder.Build();

        // Act - the factory runs on the host's own start path, so configure has to be released from
        // another thread.
        var startup = Task.Run(() => host.StartAsync());
        Assert.True(configureEntered.Wait(WaitTimeout), "The configure callback was never invoked.");

        var attachedDuringConfigure = ((IInterceptorSubject)configuredSubject!).TryGetSubjectTarget() is not null;
        releaseConfigure.Set();
        await startup.WaitAsync(WaitTimeout);

        try
        {
            // Assert - a subject target exists only once a handler has seen the attach, so its
            // absence is what proves no start could have been appended while configure still ran.
            Assert.False(attachedDuringConfigure, "The subject was attached to the context before configure ran.");
            Assert.Equal("configured", configuredSubject!.NameAtStart);
            Assert.Equal(1, configuredSubject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheConstructorTakesTheContextAndIgnoresIt_ThenItIsAttachedAndStartsOnce()
    {
        // Arrange - the constructor consumes the context argument and drops it, so the attach the
        // generated constructor would have done never happens and only the unconditional one does.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectIgnoringContextParameter>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectIgnoringContextParameter>();

            // Assert
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
    public async Task WhenTheConstructorIgnoresTheContext_ThenConfigureCompletesBeforeTheSubjectCanStart()
    {
        // Arrange - this shape gets no generated context constructor, so the attach this method
        // performs is the first one and is what makes the handler append a start. The sibling test
        // for the no-context shape asserts the same ordering; without it a configure held open past
        // the handler's start delay is observed by StartAsync as an unconfigured subject.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
        builder.Services.AddSingleton(context);

        using var configureEntered = new ManualResetEventSlim();
        using var releaseConfigure = new ManualResetEventSlim();
        SubjectIgnoringContextParameter? configuredSubject = null;

        builder.Services.AddSubject<SubjectIgnoringContextParameter>(subject =>
        {
            configuredSubject = subject;
            configureEntered.Set();
            releaseConfigure.Wait(WaitTimeout);
            subject.Name = "configured";
        });

        var host = builder.Build();

        // Act - the factory runs on the host's own start path, so configure has to be released from
        // another thread.
        var startup = Task.Run(() => host.StartAsync());
        Assert.True(configureEntered.Wait(WaitTimeout), "The configure callback was never invoked.");

        var subject = configuredSubject!;
        var attachedDuringConfigure = ((IInterceptorSubject)subject).TryGetSubjectTarget() is not null;
        var startCountDuringConfigure = subject.StartCount;
        releaseConfigure.Set();
        await startup.WaitAsync(WaitTimeout);

        try
        {
            // Assert - a subject target exists only once a handler has seen the attach, so its
            // absence is what proves no start could have been appended while configure still ran.
            Assert.False(attachedDuringConfigure, "The subject was attached to the context before configure ran.");
            Assert.Equal(0, startCountDuringConfigure);
            Assert.Equal("configured", subject.NameAtStart);
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheSubjectIsNotAHostedService_ThenItIsStillConstructedAndAttachedAtHostStart()
    {
        // Arrange - nothing resolves the singleton, so the activation is the only thing that can
        // construct it, and construction is what attaches it.
        var builder = HostingTestHost.CreateBuilder();
        var context = CreateContextWithRegistry(builder);
        builder.Services.AddSingleton(context);

        Person? constructedSubject = null;
        builder.Services.AddSubject<Person>(subject => constructedSubject = subject);

        var host = builder.Build();

        // Act
        await host.StartAsync();

        try
        {
            // Assert - read before anything resolves the singleton
            Assert.NotNull(constructedSubject);

            var registry = context.GetService<ISubjectRegistry>();
            Assert.Contains(registry.KnownSubjects, known => ReferenceEquals(known.Key, constructedSubject));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IInterceptorSubjectContext CreateContextWithRegistry(HostApplicationBuilder builder)
        => InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithRegistry()
            .WithHostedServices(builder.Services);
}
