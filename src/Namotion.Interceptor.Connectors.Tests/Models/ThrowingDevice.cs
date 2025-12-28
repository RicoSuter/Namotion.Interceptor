using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Test model that simulates a device with OnSet* methods that can throw.
/// Used to test local property failure handling in transactions.
///
/// Note: OnSet* is called BEFORE the interceptor chain, so it's called during both
/// property capture in transactions AND during commit. Use ThrowingEnabled to control
/// when exceptions should be thrown (typically enabled just before commit).
/// </summary>
[InterceptorSubject]
public partial class ThrowingDevice
{
    /// <summary>
    /// Master switch to enable/disable throwing.
    /// Set to true just before CommitAsync to test commit-time failures.
    /// </summary>
    public bool ThrowingEnabled { get; set; }

    /// <summary>
    /// Function that determines whether a property setter should throw.
    /// Only evaluated when ThrowingEnabled is true.
    /// </summary>
    public Func<string, bool>? ShouldThrow { get; set; }

    public ThrowingDevice()
    {
    }

    public partial bool PropertyA { get; set; }
    public partial bool PropertyB { get; set; }

    partial void OnSetPropertyA(ref bool value)
    {
        if (ThrowingEnabled && ShouldThrow?.Invoke(nameof(PropertyA)) == true)
        {
            throw new InvalidOperationException($"Simulated failure writing {nameof(PropertyA)}");
        }
    }

    partial void OnSetPropertyB(ref bool value)
    {
        if (ThrowingEnabled && ShouldThrow?.Invoke(nameof(PropertyB)) == true)
        {
            throw new InvalidOperationException($"Simulated failure writing {nameof(PropertyB)}");
        }
    }
}
