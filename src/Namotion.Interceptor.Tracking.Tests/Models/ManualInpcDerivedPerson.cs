using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ManualInpcDerivedPerson : ManualInpcPersonBase
{
    public partial string? FirstName { get; set; }

    [Derived]
    public string FullName => $"Name: {FirstName}";
}
