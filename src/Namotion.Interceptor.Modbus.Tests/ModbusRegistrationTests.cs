using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests;

public partial class ModbusRegistrationTests
{
    [InterceptorSubject]
    public partial class RegistrationSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    private static RegistrationSubject CreateSubject()
        => new(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle());

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateSubject());
        return services;
    }

    [Fact]
    public void WhenUnnamedSourceIsRegistered_ThenItResolvesAsSingletonHostedService()
    {
        // Arrange
        var services = CreateServices();
        services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.1");
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<ModbusSubjectClientSource>();
        var second = provider.GetRequiredService<ModbusSubjectClientSource>();

        // Assert
        Assert.Same(first, second);
        Assert.Same(provider.GetRequiredService<RegistrationSubject>(), first.RootSubject);
        Assert.Contains(first, provider.GetServices<IHostedService>());
    }

    [Fact]
    public void WhenUnnamedSourceIsRegisteredTwice_ThenInvalidOperationExceptionIsThrown()
    {
        // Arrange
        var services = CreateServices();
        services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.1");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.2"));
    }

    [Fact]
    public void WhenNamedSourcesAreRegistered_ThenEachResolvesByName()
    {
        // Arrange
        var services = CreateServices();
        services.AddKeyedModbusSubjectClientSource("first", serviceProvider => serviceProvider.GetRequiredService<RegistrationSubject>(), _ => new ModbusClientConfiguration { Host = "127.0.0.1" });
        services.AddKeyedModbusSubjectClientSource("second", _ => CreateSubject(), _ => new ModbusClientConfiguration { Host = "127.0.0.2" });
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredKeyedService<ModbusSubjectClientSource>("first");
        var second = provider.GetRequiredKeyedService<ModbusSubjectClientSource>("second");

        // Assert
        Assert.NotSame(first, second);
        Assert.Same(first, provider.GetRequiredKeyedService<ModbusSubjectClientSource>("first"));
        Assert.Equal(2, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void WhenNamedAndUnnamedSourcesAreRegistered_ThenBothResolve()
    {
        // Arrange
        var services = CreateServices();
        services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.1");
        services.AddKeyedModbusSubjectClientSource("device", _ => CreateSubject(), _ => new ModbusClientConfiguration { Host = "127.0.0.2" });
        using var provider = services.BuildServiceProvider();

        // Act
        var unnamed = provider.GetRequiredService<ModbusSubjectClientSource>();
        var named = provider.GetRequiredKeyedService<ModbusSubjectClientSource>("device");

        // Assert
        Assert.NotSame(unnamed, named);
        Assert.Same(provider.GetRequiredService<RegistrationSubject>(), unnamed.RootSubject);
        Assert.Equal(2, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void WhenNamedSourceIsRegisteredTwice_ThenInvalidOperationExceptionIsThrown()
    {
        // Arrange
        var services = CreateServices();
        services.AddKeyedModbusSubjectClientSource("device", serviceProvider => serviceProvider.GetRequiredService<RegistrationSubject>(), _ => new ModbusClientConfiguration { Host = "127.0.0.1" });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddKeyedModbusSubjectClientSource("device", serviceProvider => serviceProvider.GetRequiredService<RegistrationSubject>(), _ => new ModbusClientConfiguration { Host = "127.0.0.1" }));
    }

    [Fact]
    public void WhenSourceIsResolved_ThenConfigurationProviderRunsOnce()
    {
        // Arrange
        var services = CreateServices();
        var calls = 0;
        services.AddModbusSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<RegistrationSubject>(),
            _ =>
            {
                calls++;
                return new ModbusClientConfiguration { Host = "127.0.0.1" };
            });
        using var provider = services.BuildServiceProvider();

        // Act
        provider.GetRequiredService<ModbusSubjectClientSource>();
        _ = provider.GetServices<IHostedService>().ToList();

        // Assert
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WhenCreatingSourceFromSubject_ThenRootSubjectIsTheSubject()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        using var source = subject.CreateModbusClientSource(new ModbusClientConfiguration { Host = "127.0.0.1" }, NullLogger.Instance);

        // Assert
        Assert.Same(subject, source.RootSubject);
    }
}
