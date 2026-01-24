using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
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
}

[InterceptorSubject]
public partial class TestPerson
{
    public TestPerson()
    {
        FirstName = "";
        LastName = "";
        Scores = [];
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