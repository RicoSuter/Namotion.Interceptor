
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.SampleModel;

[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = [];
    }

    [SourcePath("opc", "FirstName")]
    public partial string? FirstName { get; set; }

    [SourcePath("opc", "LastName")]
    public partial string? LastName { get; set; }
    
    [Derived]
    [SourcePath("opc", "FullName")]
    public string FullName => $"{FirstName} {LastName}";

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial IReadOnlyCollection<Person> Children { get; set; }
}