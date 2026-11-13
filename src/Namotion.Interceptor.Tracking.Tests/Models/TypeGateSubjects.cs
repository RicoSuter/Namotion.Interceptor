using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// Used by DerivedPropertyTypeGateTests only: a type's gate is raised once per process, so each test needs
// types that no other test tracks.

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
