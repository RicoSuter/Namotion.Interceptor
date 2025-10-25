using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.Sources.Tests.Models;

[InterceptorSubject]
public partial class Person
{
    public Person()
    {
        Children = new List<Person>();
        FirstName_MaxLength = 123;
        FirstName_MaxLength_Unit = "Count";
    }

    [MaxLength(4)]
    [SourcePath("test", "FirstName")]
    public partial string? FirstName { get; set; }
    
    [PropertyAttribute(nameof(FirstName), "MaxLength")]
    public partial int FirstName_MaxLength { get; set; }
        
    [PropertyAttribute(nameof(FirstName_MaxLength), "Unit")]
    public partial string FirstName_MaxLength_Unit { get; set; }

    public partial string? LastName { get; set; }

    public partial Person? Father { get; set; }

    public partial Person? Mother { get; set; }

    public partial List<Person> Children { get; set; }
}