using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A named parent that a <see cref="ScopedChild"/> looks up through its parents.
/// </summary>
[InterceptorSubject]
public partial class ScopeHolder
{
    public partial string? Name { get; set; }

    public partial ScopedChild? Child { get; set; }
}
