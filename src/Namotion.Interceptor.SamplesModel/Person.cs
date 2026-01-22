using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.SamplesModel;

[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = [];
    }

    [Path("opc", "FirstName")]
    [Path("mqtt", "FirstName")]
    [Path("ws", "FirstName")]
    public partial string? FirstName { get; set; }

    [Path("opc", "LastName")]
    [Path("mqtt", "LastName")]
    [Path("ws", "LastName")]
    public partial string? LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial IReadOnlyCollection<Person> Children { get; set; }
}
