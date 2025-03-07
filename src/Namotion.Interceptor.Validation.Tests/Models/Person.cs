using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models
{
    [InterceptorSubject]
    public partial class Person
    {
        public Person()
        {
            Children = [];
        }

        public partial string? Id { get; init; }

        [MaxLength(4)]
        public partial string? FirstName { get; set; }

        public partial string? LastName { get; set; }

        [Derived]
        public string FullName => $"{FirstName} {LastName}";

        [Derived]
        public string FullNameWithPrefix => $"Mr. {FullName}";

        public partial Person? Father { get; set; }

        public partial Person? Mother { get; set; }

        public partial Person[] Children { get; set; }

        public override string ToString() => FullName;
    }
}