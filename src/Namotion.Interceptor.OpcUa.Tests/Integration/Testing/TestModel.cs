using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Attributes;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

[InterceptorSubject]
public partial class TestRoot
{
    public TestRoot()
    {
        ScalarNumbers = [1, 2, 3, 4, 5];
        ScalarStrings = ["Hello", "World", "OPC", "UA"];
        People = [];
        PeopleByName = new Dictionary<string, TestPerson>();
        Sensor = new TestSensor();
    }

    [Path("opc", "Connected")]
    public partial bool Connected { get; set; }

    [Path("opc", "Name")]
    public partial string Name { get; set; }

    [Path("opc", "Number")]
    public partial decimal Number { get; set; }

    [Path("opc", "ScalarNumbers")]
    public partial int[] ScalarNumbers { get; set; }

    [Path("opc", "ScalarStrings")]
    public partial string[] ScalarStrings { get; set; }

    [Path("opc", "Person")]
    public partial TestPerson Person { get; set; }

    [Path("opc", "People")]
    public partial TestPerson[] People { get; set; }

    [Path("opc", "PeopleByName")]
    public partial Dictionary<string, TestPerson>? PeopleByName { get; set; }

    [OpcUaNode("Sensor", null)]
    [OpcUaReference("HasComponent")]
    public partial TestSensor? Sensor { get; set; }
}

[InterceptorSubject]
public partial class TestPerson
{
    public TestPerson()
    {
        FirstName = "";
        LastName = "";
        Scores = [];
        Address = new TestAddress();
    }

    [Path("opc", "FirstName")]
    public partial string FirstName { get; set; }

    [Path("opc", "LastName")]
    public partial string LastName { get; set; }

    [Derived]
    [Path("opc", "FullName")]
    public string FullName => $"{FirstName} {LastName}";

    [Path("opc", "Scores")]
    public partial double[] Scores { get; set; }

    [Path("opc", "Address")]
    public partial TestAddress? Address { get; set; }
}

[InterceptorSubject]
public partial class TestAddress
{
    public TestAddress()
    {
        City = "";
        ZipCode = "";
    }

    [Path("opc", "City")]
    public partial string City { get; set; }

    [Path("opc", "ZipCode")]
    public partial string ZipCode { get; set; }
}

/// <summary>
/// Test model demonstrating OpcUaValue pattern - a VariableNode with child properties.
/// </summary>
[InterceptorSubject]
[OpcUaNode("TestSensor", null, NodeClass = OpcUaNodeClass.Variable)]
public partial class TestSensor
{
    public TestSensor()
    {
        Value = 0;
        Unit = "";
    }

    /// <summary>The OPC UA Value attribute - this is the VariableNode's value.</summary>
    [OpcUaNode("Value", null)]
    [OpcUaValue]
    public partial double Value { get; set; }

    /// <summary>Child property of the VariableNode.</summary>
    [OpcUaNode("Unit", null)]
    public partial string? Unit { get; set; }

    /// <summary>Child property of the VariableNode.</summary>
    [OpcUaNode("MinValue", null)]
    public partial double? MinValue { get; set; }

    /// <summary>Child property of the VariableNode.</summary>
    [OpcUaNode("MaxValue", null)]
    public partial double? MaxValue { get; set; }
}

/// <summary>
/// Test model for data change filter testing via configuration defaults and attributes.
/// Uses -1 as sentinel for enums and NaN for double when "not set" in attributes.
/// </summary>
[InterceptorSubject]
public partial class TestSensorData
{
    /// <summary>Temperature with absolute deadband of 0.5 via attribute.</summary>
    [OpcUaNode("Temperature", null, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.5)]
    public partial double Temperature { get; set; }

    /// <summary>Pressure with percent deadband of 2.5 via attribute.</summary>
    [OpcUaNode("Pressure", null, DeadbandType = DeadbandType.Percent, DeadbandValue = 2.5)]
    public partial double Pressure { get; set; }

    /// <summary>Status with StatusValueTimestamp trigger via attribute.</summary>
    [OpcUaNode("Status", null, DataChangeTrigger = DataChangeTrigger.StatusValueTimestamp)]
    public partial int Status { get; set; }

    /// <summary>Signal with exception-based monitoring (sampling interval 0).</summary>
    [OpcUaNode("Signal", null, SamplingInterval = 0)]
    public partial bool Signal { get; set; }

    /// <summary>Counter with no filter settings (uses defaults).</summary>
    [Path("opc", "Counter")]
    public partial int Counter { get; set; }
}

/// <summary>
/// Test model for node mapper testing with various attribute configurations.
/// </summary>
[InterceptorSubject]
public partial class TestNodeMapperModel
{
    /// <summary>Simple property with just OpcUaNode attribute.</summary>
    [OpcUaNode("SimpleProp", "http://test/")]
    public partial string SimpleProp { get; set; }

    /// <summary>Property with sampling/queue settings.</summary>
    [OpcUaNode("MonitoredProp", null, SamplingInterval = 500, QueueSize = 10)]
    public partial double MonitoredProp { get; set; }

    /// <summary>Property with data change filter settings.</summary>
    [OpcUaNode("FilteredProp", null, DataChangeTrigger = DataChangeTrigger.StatusValueTimestamp, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.5)]
    public partial double FilteredProp { get; set; }

    /// <summary>Property with discard oldest false.</summary>
    [OpcUaNode("QueueProp", null, DiscardOldest = DiscardOldestMode.False)]
    public partial int QueueProp { get; set; }

    /// <summary>Property with type definition.</summary>
    [OpcUaNode("TypedProp", null, TypeDefinition = "AnalogItemType", TypeDefinitionNamespace = "http://opcfoundation.org/UA/")]
    public partial double TypedProp { get; set; }

    /// <summary>Property with OpcUaReference attribute.</summary>
    [OpcUaNode("RefProp", null)]
    [OpcUaReference("HasComponent")]
    public partial TestRefChild RefProp { get; set; }

    /// <summary>Simple double property for value testing (OpcUaValue moved to TestVariableChild).</summary>
    [OpcUaNode("ValueProp", null)]
    public partial double ValueProp { get; set; }

    /// <summary>Property with modelling rule.</summary>
    [OpcUaNode("MandatoryProp", null, ModellingRule = ModellingRule.Mandatory)]
    public partial string MandatoryProp { get; set; }

    /// <summary>Property with NodeClass override.</summary>
    [OpcUaNode("VariableClassProp", null, NodeClass = OpcUaNodeClass.Variable)]
    public partial TestVariableChild VariableClassProp { get; set; }

    /// <summary>Property with no OPC UA attributes (for negative testing).</summary>
    public partial string PlainProp { get; set; }

    /// <summary>Property with EventNotifier explicitly set to 0 (no events).</summary>
    [OpcUaNode("EventNotifierZeroProp", null, EventNotifier = 0)]
    public partial int EventNotifierZeroProp { get; set; }

    public TestNodeMapperModel()
    {
        SimpleProp = "";
        MonitoredProp = 0;
        FilteredProp = 0;
        QueueProp = 0;
        TypedProp = 0;
        RefProp = new TestRefChild();
        ValueProp = 0;
        MandatoryProp = "";
        VariableClassProp = new TestVariableChild();
        PlainProp = "";
        EventNotifierZeroProp = 0;
    }
}

[InterceptorSubject]
public partial class TestRefChild
{
    [OpcUaNode("Value", null)]
    public partial double Value { get; set; }

    public TestRefChild()
    {
        Value = 0;
    }
}

[InterceptorSubject]
[OpcUaNode("TestVariableChild", null, NodeClass = OpcUaNodeClass.Variable)]
public partial class TestVariableChild
{
    [OpcUaNode("Value", null)]
    [OpcUaValue]
    public partial double Value { get; set; }

    public TestVariableChild()
    {
        Value = 0;
    }
}

/// <summary>Invalid model: has [OpcUaValue] without NodeClass = Variable on containing class.</summary>
[InterceptorSubject]
public partial class TestInvalidOpcUaValueModel
{
    [OpcUaValue] // Invalid - class doesn't have NodeClass = Variable
    public partial double InvalidValue { get; set; }

    public TestInvalidOpcUaValueModel()
    {
        InvalidValue = 0;
    }
}

/// <summary>Child item for collection/dictionary tests with class-level OpcUaNode attribute.</summary>
[InterceptorSubject]
[OpcUaNode("TestCollectionChild", null, TypeDefinition = "CollectionItemType")]
public partial class TestCollectionChild
{
    public partial double Value { get; set; }

    public TestCollectionChild()
    {
        Value = 0;
    }
}

/// <summary>Parent model with collection and dictionary properties for class-level config tests.</summary>
[InterceptorSubject]
public partial class TestCollectionParent
{
    public partial List<TestCollectionChild>? Items { get; set; }
    public partial Dictionary<string, TestCollectionChild>? ItemsByKey { get; set; }

    public TestCollectionParent()
    {
        Items = [];
        ItemsByKey = [];
    }
}

/// <summary>Analog signal model demonstrating VariableType pattern with [OpcUaValue].</summary>
[InterceptorSubject]
[OpcUaNode("TestAnalogSignal", null, TypeDefinition = "AnalogSignalVariableType", NodeClass = OpcUaNodeClass.Variable)]
public partial class TestAnalogSignal
{
    [OpcUaNode("ActualValue", null)]
    [OpcUaValue]
    public partial double ActualValue { get; set; }

    [OpcUaNode("MinValue", null)]
    public partial double? MinValue { get; set; }

    [OpcUaNode("MaxValue", null)]
    public partial double? MaxValue { get; set; }

    public TestAnalogSignal()
    {
        ActualValue = 0;
    }
}

/// <summary>Sensor model with analog signal for e2e OpcUaValue testing.</summary>
[InterceptorSubject]
public partial class TestSensorWithAnalog
{
    [OpcUaNode("Temperature", null)]
    [OpcUaReference("HasComponent")]
    public partial TestAnalogSignal? Temperature { get; set; }

    public TestSensorWithAnalog()
    {
        Temperature = new TestAnalogSignal();
    }
}