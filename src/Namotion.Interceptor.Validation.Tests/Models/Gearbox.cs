using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

[InterceptorSubject]
public partial class Gearbox
{
    public partial int? Gear { get; set; }

    public partial string? Label { get; set; }

    public partial GearboxMode Mode { get; set; }

    public partial Guid SerialNumber { get; set; }
}

public enum GearboxMode
{
    Manual,
    Automatic
}
