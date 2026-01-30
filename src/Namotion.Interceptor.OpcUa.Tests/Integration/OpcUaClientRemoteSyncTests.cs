using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Configuration tests for OPC UA client remote sync options.
/// Lifecycle tests are covered by OpcUaBidirectionalGraphTests and OpcUaClientGraphTests.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaClientRemoteSyncTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaClientRemoteSyncTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Configuration_EnableModelChangeEvents_DefaultsFalse()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnableModelChangeEvents);
    }

    [Fact]
    public void Configuration_EnablePeriodicResync_DefaultsFalse()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnablePeriodicResync);
    }

    [Fact]
    public void Configuration_PeriodicResyncInterval_DefaultsTo30Seconds()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.PeriodicResyncInterval);
    }

    [Fact]
    public void Configuration_CanEnableRemoteSyncFeatures()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            EnableModelChangeEvents = true,
            EnablePeriodicResync = true,
            PeriodicResyncInterval = TimeSpan.FromSeconds(5)
        };

        // Assert
        Assert.True(configuration.EnableModelChangeEvents);
        Assert.True(configuration.EnablePeriodicResync);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.PeriodicResyncInterval);
    }

    [Fact]
    public void Configuration_EnableLiveSync_DefaultsFalse()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnableLiveSync);
    }

    [Fact]
    public void Configuration_EnableRemoteNodeManagement_DefaultsFalse()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnableRemoteNodeManagement);
    }

    [Fact]
    public void Configuration_CanEnableAllLiveSyncOptions()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            EnableLiveSync = true,
            EnableRemoteNodeManagement = true,
            EnableModelChangeEvents = true,
            EnablePeriodicResync = true,
            PeriodicResyncInterval = TimeSpan.FromSeconds(60)
        };

        // Assert
        Assert.True(configuration.EnableLiveSync);
        Assert.True(configuration.EnableRemoteNodeManagement);
        Assert.True(configuration.EnableModelChangeEvents);
        Assert.True(configuration.EnablePeriodicResync);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.PeriodicResyncInterval);
    }
}
