using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

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