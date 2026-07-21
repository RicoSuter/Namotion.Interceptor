using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Test model whose OnValueChanging hook clamps the incoming value, used to pin the origin
/// semantics of transformed trigger values: a write whose stored value differs from the incoming
/// value publishes as local origin, because the local model computed it.
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
    }
}
