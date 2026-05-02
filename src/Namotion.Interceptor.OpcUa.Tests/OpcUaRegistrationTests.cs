using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests;

public partial class OpcUaRegistrationTests
{
    [InterceptorSubject]
    public partial class RegistrationTestSubject
    {
        public partial string Name { get; set; }
    }

    private static RegistrationTestSubject CreateSubject()
    {
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        return new RegistrationTestSubject(context);
    }

    private static OpcUaClientConfiguration CreateClientConfig() => new()
    {
        ServerUrl = "opc.tcp://localhost:4840",
        TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
        ValueConverter = new OpcUaValueConverter(),
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
    };

    private static OpcUaServerConfiguration CreateServerConfig() => new()
    {
        ValueConverter = new OpcUaValueConverter()
    };

    [Fact]
    public void WhenUnnamedClientIsRegistered_ThenResolvesAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateClientConfig());

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var first = serviceProvider.GetRequiredService<IOpcUaSubjectClientSource>();
        var second = serviceProvider.GetRequiredService<IOpcUaSubjectClientSource>();

        // Assert
        Assert.NotNull(first);
        Assert.IsType<OpcUaSubjectClientSource>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenUnnamedServerIsRegistered_ThenResolvesAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateServerConfig());

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var first = serviceProvider.GetRequiredService<IOpcUaSubjectServer>();
        var second = serviceProvider.GetRequiredService<IOpcUaSubjectServer>();

        // Assert
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenNamedClientIsRegistered_ThenResolvesByKey()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectClientSource(
            "server1",
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateClientConfig());

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>("server1");

        // Assert
        Assert.NotNull(resolved);
        Assert.IsType<OpcUaSubjectClientSource>(resolved);
    }

    [Fact]
    public void WhenNamedServerIsRegistered_ThenResolvesByKey()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectServer(
            "server1",
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateServerConfig());

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectServer>("server1");

        // Assert
        Assert.NotNull(resolved);
    }

    [Fact]
    public void WhenTwoNamedClientsRegistered_ThenResolveIndependently()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectClientSource(
            "server1",
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateClientConfig());
        services.AddKeyedOpcUaSubjectClientSource(
            "server2",
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateClientConfig());

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var first = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>("server1");
        var second = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>("server2");

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void WhenDuplicateUnnamedClient_ThenThrows()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateClientConfig());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddOpcUaSubjectClientSource(
                sp => sp.GetRequiredService<RegistrationTestSubject>(),
                _ => CreateClientConfig()));
    }

    [Fact]
    public void WhenDuplicateUnnamedServer_ThenThrows()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateServerConfig());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddOpcUaSubjectServer(
                sp => sp.GetRequiredService<RegistrationTestSubject>(),
                _ => CreateServerConfig()));
    }

    [Fact]
    public void WhenDuplicateNamedClient_ThenThrows()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectClientSource(
            "server1",
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateClientConfig());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddKeyedOpcUaSubjectClientSource(
                "server1",
                sp => sp.GetRequiredService<RegistrationTestSubject>(),
                _ => CreateClientConfig()));
    }

    [Fact]
    public void WhenDuplicateNamedServer_ThenThrows()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectServer(
            "server1",
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            _ => CreateServerConfig());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddKeyedOpcUaSubjectServer(
                "server1",
                sp => sp.GetRequiredService<RegistrationTestSubject>(),
                _ => CreateServerConfig()));
    }

    [Fact]
    public void WhenSimpleGenericClientIsRegistered_ThenResolvesSubjectFromDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddOpcUaSubjectClientSource<RegistrationTestSubject>(
            serverUrl: "opc.tcp://localhost:4840",
            sourceName: "opc",
            rootName: "Root");

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredService<IOpcUaSubjectClientSource>();

        // Assert
        Assert.NotNull(resolved);
        Assert.IsType<OpcUaSubjectClientSource>(resolved);
    }

    [Fact]
    public void WhenSimpleGenericServerIsRegistered_ThenResolvesSubjectFromDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddOpcUaSubjectServer<RegistrationTestSubject>(
            sourceName: "opc",
            rootName: "Root");

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredService<IOpcUaSubjectServer>();

        // Assert
        Assert.NotNull(resolved);
    }

    [Fact]
    public void WhenSimpleGenericKeyedClientIsRegistered_ThenResolvesSubjectFromDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectClientSource<RegistrationTestSubject>(
            name: "server1",
            serverUrl: "opc.tcp://localhost:4840",
            sourceName: "opc",
            rootName: "Root");

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>("server1");

        // Assert
        Assert.NotNull(resolved);
        Assert.IsType<OpcUaSubjectClientSource>(resolved);
    }

    [Fact]
    public void WhenSimpleGenericKeyedServerIsRegistered_ThenResolvesSubjectFromDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());
        services.AddKeyedOpcUaSubjectServer<RegistrationTestSubject>(
            name: "server1",
            sourceName: "opc",
            rootName: "Root");

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectServer>("server1");

        // Assert
        Assert.NotNull(resolved);
    }

    [Fact]
    public void WhenTwoServersAreRegisteredForSameSubject_ThenOpcUaVariableKeysDiffer()
    {
        // Arrange
        var subject = CreateSubject();
        var configuration = new OpcUaServerConfiguration { ValueConverter = new OpcUaValueConverter() };
        var logger = NullLogger<OpcUaSubjectServerBackgroundService>.Instance;

        // Act
        var first = new OpcUaSubjectServerBackgroundService(subject, configuration, logger);
        var second = new OpcUaSubjectServerBackgroundService(subject, configuration, logger);

        // Assert
        Assert.NotEqual(first.OpcUaVariableKey, second.OpcUaVariableKey);
        Assert.StartsWith("OpcUaVariable:", first.OpcUaVariableKey);
        Assert.StartsWith("OpcUaVariable:", second.OpcUaVariableKey);
    }
}
