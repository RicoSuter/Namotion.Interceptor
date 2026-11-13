using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// Used by DerivedPropertyTypeGateTests only. A type's gate stays raised for the process once a derived property
// read a subject of the type, so no test may let a derived property read UntrackedGateSubject.

[InterceptorSubject]
public partial class UntrackedGateSubject
{
    public partial string? Value { get; set; }
}

[InterceptorSubject]
public partial class TrackedGateSource
{
    public partial string? Value { get; set; }
}

[InterceptorSubject]
public partial class TrackedGateReader
{
    public TrackedGateSource? Source;

    [Derived]
    public string? SourceValue => Source?.Value;
}

[InterceptorSubject]
public partial class TrackedGateRoot
{
    public partial TrackedGateReader? Reader { get; set; }
}
