using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    /// <summary>
    /// A derived property with a setter, which transaction capture skips, whose dependent reads a plain
    /// property that capture does take.
    /// </summary>
    [InterceptorSubject]
    public partial class TransactionCascadeSubject
    {
        public partial string? Plain { get; set; }

        [Derived]
        public partial string? DerivedWithSetter { get; set; }

        [Derived]
        public string Combined => $"{Plain}|{DerivedWithSetter}";
    }
}
