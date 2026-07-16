using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model whose OnValueChanging hook clamps the incoming value to 0..100. Used to drive the
/// correction-detection cases: an inbound value that the hook projects onto the already-stored
/// value is equality-suppressed, and that is the diverged case a correction resolves.
/// </summary>
[InterceptorSubject]
public partial class ClampingDevice
{
    public partial int Value { get; set; }

    partial void OnValueChanging(ref int newValue, ref bool cancel)
    {
        if (newValue > 100)
        {
            newValue = 100;
        }
        else if (newValue < 0)
        {
            newValue = 0;
        }
    }
}
