using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for OPC UA data change filter configuration via attributes and configuration defaults.
/// Tests use TestSensorData model which has properties with different [OpcUaNode] attribute settings:
/// - Temperature: DeadbandType.Absolute, DeadbandValue=0.5
/// - Pressure: DeadbandType.Percent, DeadbandValue=2.5
/// - Status: DataChangeTrigger.StatusValueTimestamp
/// - Signal: SamplingInterval=0 (exception-based monitoring)
/// - Counter: No filter settings (uses defaults)
/// </summary>
public class DataChangeFilterTests
{
    private static OpcUaClientConfiguration CreateValidConfiguration()
    {
        return new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };
    }

    [Fact]
    public void CreateMonitoredItem_WithNoFilterOptions_DoesNotSetFilter()
    {
        // Arrange - Counter has no filter settings in attribute, config has no defaults
        var config = CreateValidConfiguration();
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - No filter when no options specified
        Assert.Null(item.Filter);
    }

    [Fact]
    public void CreateMonitoredItem_WithAbsoluteDeadbandAttribute_SetsFilter()
    {
        // Arrange - Temperature has [OpcUaNode] with DeadbandType.Absolute and DeadbandValue=0.5
        var config = CreateValidConfiguration();
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;
        var nodeId = new NodeId("Temperature", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Absolute, filter.DeadbandType);
        Assert.Equal(0.5, filter.DeadbandValue);
    }

    [Fact]
    public void CreateMonitoredItem_WithPercentDeadbandAttribute_SetsFilter()
    {
        // Arrange - Pressure has [OpcUaNode] with DeadbandType.Percent and DeadbandValue=2.5
        var config = CreateValidConfiguration();
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Pressure")!;
        var nodeId = new NodeId("Pressure", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Percent, filter.DeadbandType);
        Assert.Equal(2.5, filter.DeadbandValue);
    }

    [Fact]
    public void CreateMonitoredItem_WithDataChangeTriggerAttribute_SetsFilter()
    {
        // Arrange - Status has [OpcUaNode] with DataChangeTrigger.StatusValueTimestamp
        var config = CreateValidConfiguration();
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Status")!;
        var nodeId = new NodeId("Status", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, filter.Trigger);
    }

    [Fact]
    public void CreateMonitoredItem_WithExceptionBasedMonitoringAttribute_SetsSamplingIntervalZero()
    {
        // Arrange - Signal has [OpcUaNode] with SamplingInterval=0
        var config = CreateValidConfiguration();
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Signal")!;
        var nodeId = new NodeId("Signal", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert
        Assert.Equal(0, item.SamplingInterval);
    }

    [Fact]
    public void CreateMonitoredItem_WithAttributeOverridingConfigDefault_UsesAttributeValue()
    {
        // Arrange - Config has defaults, but Temperature attribute has specific values
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultDeadbandType = DeadbandType.Percent,  // Config says Percent
            DefaultDeadbandValue = 10.0                   // Config says 10.0
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        // Temperature has [OpcUaNode] with DeadbandType.Absolute and DeadbandValue=0.5
        var property = registeredSubject.TryGetProperty("Temperature")!;
        var nodeId = new NodeId("Temperature", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - Attribute values (Absolute, 0.5) override config defaults (Percent, 10.0)
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Absolute, filter.DeadbandType);
        Assert.Equal(0.5, filter.DeadbandValue);
    }

    [Fact]
    public void CreateMonitoredItem_WithNoAttributeUsesConfigDefault()
    {
        // Arrange - Counter has no filter settings in attribute, uses config defaults
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultDeadbandType = DeadbandType.Percent,
            DefaultDeadbandValue = 5.0
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - Uses config defaults since Counter has no filter settings in attribute
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Percent, filter.DeadbandType);
        Assert.Equal(5.0, filter.DeadbandValue);
    }

    [Fact]
    public void CreateMonitoredItem_WithQueueSizeConfig_SetsQueueSize()
    {
        // Arrange
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultQueueSize = 10
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert
        Assert.Equal(10u, item.QueueSize);
    }

    [Fact]
    public void CreateMonitoredItem_WithDiscardOldestConfig_SetsDiscardOldest()
    {
        // Arrange
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultDiscardOldest = false
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert
        Assert.False(item.DiscardOldest);
    }

    [Fact]
    public void CreateMonitoredItem_WithDataChangeTriggerStatusExplicit_SetsStatusTrigger()
    {
        // Arrange - Explicitly set Status trigger (value 0) - should not be confused with sentinel
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultDataChangeTrigger = DataChangeTrigger.Status // Status = 0
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - Should be Status (0), not confused with sentinel (-1)
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal(DataChangeTrigger.Status, filter.Trigger);
    }

    [Fact]
    public void CreateMonitoredItem_WithDeadbandTypeNoneExplicit_SetsNoneDeadband()
    {
        // Arrange - Explicitly set None deadband (value 0) - should not be confused with sentinel
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultDeadbandType = DeadbandType.None, // None = 0
            DefaultDeadbandValue = 1.0 // Need a value to trigger filter creation
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - Should be None (0), not confused with sentinel (-1)
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.None, filter.DeadbandType);
    }

    [Fact]
    public void CreateMonitoredItem_WithSamplingIntervalMinusOne_SetsServerDecides()
    {
        // Arrange - SamplingInterval -1 means "server decides" (valid value, not sentinel)
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            DefaultSamplingInterval = -1 // Server decides
        };
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Counter")!;
        var nodeId = new NodeId("Counter", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - Should be -1, not confused with sentinel (int.MinValue)
        Assert.Equal(-1, item.SamplingInterval);
    }

    [Fact]
    public void CreateMonitoredItem_CombinesMultipleAttributeSettings()
    {
        // Arrange - Temperature has multiple settings: DeadbandType, DeadbandValue
        var config = CreateValidConfiguration();
        var subject = new TestSensorData(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;
        var nodeId = new NodeId("Temperature", 2);

        // Act
        var item = MonitoredItemFactory.Create(config, nodeId, property);

        // Assert - Both DeadbandType and DeadbandValue are set from attribute
        Assert.NotNull(item.Filter);
        var filter = item.Filter as DataChangeFilter;
        Assert.NotNull(filter);
        Assert.Equal((uint)DeadbandType.Absolute, filter.DeadbandType);
        Assert.Equal(0.5, filter.DeadbandValue);
        // Trigger should default to StatusValue when not specified
        Assert.Equal(DataChangeTrigger.StatusValue, filter.Trigger);
    }
}
