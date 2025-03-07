using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models
{
    // test different property types

    [InterceptorSubject]
    public partial class Address
    {
        // required property (tracked)
        public required partial string? City { get; set; }
     
        // not partial (not tracked)
        public string? Street { get; set; }

        // derived property (trackable)
        [Derived]
        public string FullAddress => $"{Street}, {City}";

        // get only (tracked)
        public partial string? GetOnly { get; }

        // set only (tracked)
        public partial string? SetOnly { get; }
    }
}