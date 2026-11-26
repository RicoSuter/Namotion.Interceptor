using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

/// <summary>A subject with two references and a collection of its own type, to build chains of parents.</summary>
[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = [];
    }

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial Person[] Children { get; set; }
}
