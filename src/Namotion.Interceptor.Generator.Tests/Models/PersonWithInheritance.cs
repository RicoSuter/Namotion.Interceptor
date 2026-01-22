using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonBase
{
    public partial string? FirstName { get; set; }
}

[InterceptorSubject]
public partial class Employee : PersonBase
{
    public partial string? Department { get; set; }
}
