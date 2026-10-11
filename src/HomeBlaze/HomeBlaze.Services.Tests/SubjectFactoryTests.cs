using HomeBlaze.Services.Tests.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Tests;

public class SubjectFactoryTests
{
    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHomeBlazeServices();
        return services.BuildServiceProvider();
    }

    private static ISubjectRegistry GetRegistry(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();
    }

    [Fact]
    public void WhenTypeHasAContextConstructor_ThenTheCreatedSubjectIsKnownToTheRootContext()
    {
        // Arrange
        using var serviceProvider = CreateServices();
        var factory = serviceProvider.GetRequiredService<SubjectFactory>();

        // Act
        var subject = factory.CreateSubject(typeof(FactoryContextSubject));

        // Assert
        Assert.Contains(subject, GetRegistry(serviceProvider).KnownSubjects.Keys);
    }

    [Fact]
    public void WhenCreatedSubjectIsDiscarded_ThenItIsNoLongerKnownToTheRootContext()
    {
        // Arrange
        using var serviceProvider = CreateServices();
        var factory = serviceProvider.GetRequiredService<SubjectFactory>();
        var registry = GetRegistry(serviceProvider);
        var discardedSubject = factory.CreateSubject(typeof(FactoryContextSubject));
        var keptSubject = factory.CreateSubject(typeof(FactoryContextSubject));

        // Act
        factory.DiscardSubject(discardedSubject);

        // Assert
        Assert.DoesNotContain(discardedSubject, registry.KnownSubjects.Keys);
        Assert.Contains(keptSubject, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenSubjectIsDiscardedTwice_ThenTheSecondCallDoesNothing()
    {
        // Arrange
        using var serviceProvider = CreateServices();
        var factory = serviceProvider.GetRequiredService<SubjectFactory>();
        var subject = factory.CreateSubject(typeof(FactoryContextSubject));
        factory.DiscardSubject(subject);

        // Act
        factory.DiscardSubject(subject);

        // Assert
        Assert.DoesNotContain(subject, GetRegistry(serviceProvider).KnownSubjects.Keys);
    }

    [Fact]
    public void WhenTypeHasOnlyADependencyInjectionConstructor_ThenTheCreatedSubjectIsDetachedAndDiscardingItDoesNothing()
    {
        // Arrange
        using var serviceProvider = CreateServices();
        var factory = serviceProvider.GetRequiredService<SubjectFactory>();
        var registry = GetRegistry(serviceProvider);
        var keptSubject = factory.CreateSubject(typeof(FactoryContextSubject));
        var subject = factory.CreateSubject(typeof(FactoryDependencySubject));
        var knownSubjects = registry.KnownSubjects.Keys.ToArray();

        // Act
        factory.DiscardSubject(subject);

        // Assert
        Assert.DoesNotContain(subject, knownSubjects);
        Assert.Contains(keptSubject, knownSubjects);
        Assert.Equal(knownSubjects, registry.KnownSubjects.Keys.ToArray());
    }

    [Fact]
    public void WhenTypeIsNotASubject_ThenCreatingItThrows()
    {
        // Arrange
        using var serviceProvider = CreateServices();
        var factory = serviceProvider.GetRequiredService<SubjectFactory>();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateSubject(typeof(NotASubject)));
        Assert.Contains(nameof(NotASubject), exception.Message);
    }

    private sealed class NotASubject;
}
