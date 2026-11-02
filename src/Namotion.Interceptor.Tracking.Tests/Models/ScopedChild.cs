using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Computes derived values from the nearest <see cref="ScopeHolder"/> parent, so what they read
/// depends on where the subject is in the graph. The parents themselves are not a tracked property.
/// </summary>
[InterceptorSubject]
public partial class ScopedChild
{
    [Derived]
    public string? ScopeName => this.TryGetFirstParent<ScopeHolder>()?.Name;

    [Derived]
    public ScopeHolder? Scope => this.TryGetFirstParent<ScopeHolder>();
}
