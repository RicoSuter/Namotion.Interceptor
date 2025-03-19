using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Sources.Tests.Models;

[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = new List<Person>();
    }

    [MaxLength(4)]
    public partial string? FirstName { get; set; }

    public partial string? LastName { get; set; }

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial IReadOnlyCollection<Person> Children { get; set; }
}