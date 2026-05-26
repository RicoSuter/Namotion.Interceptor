using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model with a derived property whose getter writes to another property as a side
/// effect. Used to verify that side-effect writes inside a cascade-triggered recalc resolve
/// their timestamp against the outer scope rather than inheriting the cascade trigger's
/// captured value (the deliberate semantic of the threading-based cascade design).
/// </summary>
[InterceptorSubject]
public partial class SideEffectWritePerson
{
    private int _sideEffectCounter;

    public partial string? Name { get; set; }

    public partial string? SideEffectTarget { get; set; }

    [Derived]
    public string Greeting
    {
        get
        {
            // Use a varying value so the PropertyValueEqualityCheckHandler does not
            // short-circuit the write on subsequent cascade passes (it skips writes whose
            // new value equals the current value). The test needs the side-effect write to
            // reach the terminal SetWriteTimestamp on every cascade.
            SideEffectTarget = "from-getter-" + Interlocked.Increment(ref _sideEffectCounter);
            return $"Hello, {Name}";
        }
    }
}
