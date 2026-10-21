using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

[InterceptorSubject]
public partial class Gearbox
{
    public partial int? Gear { get; set; }

    public partial string? Label { get; set; }
}
