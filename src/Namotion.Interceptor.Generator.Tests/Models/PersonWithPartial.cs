using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithPartial
{
    public partial string? FirstName { get; set; }
}