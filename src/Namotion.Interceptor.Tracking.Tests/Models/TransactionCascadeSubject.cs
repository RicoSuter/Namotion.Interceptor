using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class TransactionCascadeSubject
{
    public partial string? Plain { get; set; }

    [Derived]
    public partial string? DerivedWithSetter { get; set; }

    [Derived]
    public string Combined => $"{Plain}|{DerivedWithSetter}";
}
