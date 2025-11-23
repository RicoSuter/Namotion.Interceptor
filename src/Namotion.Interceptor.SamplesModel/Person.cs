using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.SamplesModel;

[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = [];
    }

    [SourcePath("opc", "FirstName")]
    [SourcePath("mqtt", "FirstName")]
    public partial string? FirstName { get; set; }

    [SourcePath("opc", "LastName")]
    [SourcePath("mqtt", "LastName")]
    public partial string? LastName { get; set; }

    [Derived]
    [SourcePath("opc", "FullName")]
    [SourcePath("mqtt", "FullName")]
    public string FullName => $"{FirstName} {LastName}";

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial IReadOnlyCollection<Person> Children { get; set; }
}
