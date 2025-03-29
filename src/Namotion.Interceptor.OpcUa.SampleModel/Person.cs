
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.SampleModel;

[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = [];
    }

    [SourceName("opc", "FirstName")]
    public partial string? FirstName { get; set; }

    [SourceName("opc", "LastName")]
    public partial string? LastName { get; set; }

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial IReadOnlyCollection<Person> Children { get; set; }
}