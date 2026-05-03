using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ConditionalPerson
{
    public partial bool UseFirstName { get; set; }

    public partial string? FirstName { get; set; }

    public partial string? LastName { get; set; }

    [Derived]
    public string Display => UseFirstName ? (FirstName ?? "") : (LastName ?? "");
}
