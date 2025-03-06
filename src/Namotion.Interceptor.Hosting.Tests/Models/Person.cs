using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models
{
    [InterceptorSubject]
    public partial class Person
    {
        public partial string? FirstName { get; set; }

        public partial string? LastName { get; set; }
    }
}