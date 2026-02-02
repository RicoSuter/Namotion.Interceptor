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
    public void Configuration_EnableGraphChangeSubscription_DefaultsFalse()
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
        Assert.False(configuration.EnableGraphChangeSubscription);
    }

    [Fact]
    public void Configuration_EnablePeriodicGraphBrowsing_DefaultsFalse()
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
        Assert.False(configuration.EnablePeriodicGraphBrowsing);
    }

    [Fact]
    public void Configuration_PeriodicGraphBrowsingInterval_DefaultsTo30Seconds()
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
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.PeriodicGraphBrowsingInterval);
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
            EnableGraphChangeSubscription = true,
            EnablePeriodicGraphBrowsing = true,
            PeriodicGraphBrowsingInterval = TimeSpan.FromSeconds(5)
        };

        // Assert
        Assert.True(configuration.EnableGraphChangeSubscription);
        Assert.True(configuration.EnablePeriodicGraphBrowsing);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.PeriodicGraphBrowsingInterval);
    }

    [Fact]
    public void Configuration_EnableGraphChangePublishing_DefaultsFalse()
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
        Assert.False(configuration.EnableGraphChangePublishing);
    }

    [Fact]
    public void Configuration_CanEnableAllGraphSyncOptions()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            EnableGraphChangePublishing = true,
            EnableGraphChangeSubscription = true,
            EnablePeriodicGraphBrowsing = true,
            PeriodicGraphBrowsingInterval = TimeSpan.FromSeconds(60)
        };

        // Assert
        Assert.True(configuration.EnableGraphChangePublishing);
        Assert.True(configuration.EnableGraphChangeSubscription);
        Assert.True(configuration.EnablePeriodicGraphBrowsing);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.PeriodicGraphBrowsingInterval);
    }
}
