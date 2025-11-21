using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Paths.Attributes;

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

    [SourcePath("opc", "Connected")]
    public partial bool Connected { get; set; }

    [SourcePath("opc", "Name")]
    public partial string Name { get; set; }

    [SourcePath("opc", "Number")]
    public partial decimal Number { get; set; }

    [SourcePath("opc", "ScalarNumbers")]
    public partial int[] ScalarNumbers { get; set; }
    
    [SourcePath("opc", "ScalarStrings")]
    public partial string[] ScalarStrings { get; set; }

    [SourcePath("opc", "Person")]
    public partial TestPerson Person { get; set; }

    [SourcePath("opc", "People")]
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

    [SourcePath("opc", "FirstName")]
    public partial string FirstName { get; set; }

    [SourcePath("opc", "LastName")]
    public partial string LastName { get; set; }

    [Derived]
    [SourcePath("opc", "FullName")]
    public string FullName => $"{FirstName} {LastName}";

    [SourcePath("opc", "Scores")]
    public partial double[] Scores { get; set; }
}