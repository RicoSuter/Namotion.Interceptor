using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests;

public class OpcUaDiDefaultsTests
{
    private static IServiceProvider CreateServiceProvider()
        => new ServiceCollection().AddLogging().BuildServiceProvider();

    [Fact]
    public void WhenClientConfigLeavesDefaults_ThenDiFillsTypeResolverAndTelemetry()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        var configuration = new OpcUaClientConfiguration { ServerUrl = "opc.tcp://localhost:4840" };

        // Act
        OpcUaSubjectExtensions.ApplyClientDiDefaults(configuration, serviceProvider);

        // Assert
        Assert.NotNull(configuration.TypeResolver);
        Assert.NotNull(configuration.TelemetryContext);
        Assert.NotSame(NullTelemetryContext.Instance, configuration.TelemetryContext);
    }

    [Fact]
    public void WhenClientTelemetrySetToNullContext_ThenDiKeepsIt()
    {
        // Arrange - explicitly choosing the no-op context must be respected, not replaced by DI logging.
        var serviceProvider = CreateServiceProvider();
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TelemetryContext = NullTelemetryContext.Instance
        };

        // Act
        OpcUaSubjectExtensions.ApplyClientDiDefaults(configuration, serviceProvider);

        // Assert
        Assert.Same(NullTelemetryContext.Instance, configuration.TelemetryContext);
    }

    [Fact]
    public void WhenClientConfigSetsTypeResolverAndTelemetry_ThenDiDoesNotClobberThem()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        var telemetry = new Mock<ITelemetryContext>().Object;
        var typeResolver = new OpcUaTypeResolver(NullLogger.Instance);
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TelemetryContext = telemetry,
            TypeResolver = typeResolver
        };

        // Act
        OpcUaSubjectExtensions.ApplyClientDiDefaults(configuration, serviceProvider);

        // Assert
        Assert.Same(telemetry, configuration.TelemetryContext);
        Assert.Same(typeResolver, configuration.TypeResolver);
    }

    [Fact]
    public void WhenServerConfigLeavesDefaults_ThenDiFillsTelemetry()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        var configuration = new OpcUaServerConfiguration();

        // Act
        OpcUaSubjectExtensions.ApplyServerDiDefaults(configuration, serviceProvider);

        // Assert
        Assert.NotNull(configuration.TelemetryContext);
        Assert.NotSame(NullTelemetryContext.Instance, configuration.TelemetryContext);
    }

    [Fact]
    public void WhenServerTelemetrySetToNullContext_ThenDiKeepsIt()
    {
        // Arrange - explicitly choosing the no-op context must be respected, not replaced by DI logging.
        var serviceProvider = CreateServiceProvider();
        var configuration = new OpcUaServerConfiguration { TelemetryContext = NullTelemetryContext.Instance };

        // Act
        OpcUaSubjectExtensions.ApplyServerDiDefaults(configuration, serviceProvider);

        // Assert
        Assert.Same(NullTelemetryContext.Instance, configuration.TelemetryContext);
    }

    [Fact]
    public void WhenServerConfigSetsTelemetry_ThenDiDoesNotClobberIt()
    {
        // Arrange
        var serviceProvider = CreateServiceProvider();
        var telemetry = new Mock<ITelemetryContext>().Object;
        var configuration = new OpcUaServerConfiguration { TelemetryContext = telemetry };

        // Act
        OpcUaSubjectExtensions.ApplyServerDiDefaults(configuration, serviceProvider);

        // Assert
        Assert.Same(telemetry, configuration.TelemetryContext);
    }
}
