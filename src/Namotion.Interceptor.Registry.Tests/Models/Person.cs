using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models
{
    [InterceptorSubject]
    public partial class Person
    {
        public Person()
        {
            Children = [];
            FirstName_MaxLength = 123;
            FirstName_MaxLength_Unit = "Count";
        }

        public partial string? Id { get; init; }

        [MaxLength(4)]
        public partial string? FirstName { get; set; }
        
        [PropertyAttribute(nameof(FirstName), "MaxLength")]
        public partial int FirstName_MaxLength { get; set; }
        
        [PropertyAttribute(nameof(FirstName_MaxLength), "Unit")]
        public partial string FirstName_MaxLength_Unit { get; set; }

        public partial string? LastName { get; set; }

        [Derived]
        public string FullName => FirstName is null && LastName is null
            ? "NA"
            : $"{FirstName} {LastName}".Trim();

        [Derived]
        public string FullNameWithPrefix => $"Mr. {FullName}";
        
        public partial Person? Father { get; set; }

        public partial Person? Mother { get; set; }

        public partial Person[] Children { get; set; }

        public override string ToString() => FullName;

        protected string ProtectedProperty => "Hidden";
    }
}