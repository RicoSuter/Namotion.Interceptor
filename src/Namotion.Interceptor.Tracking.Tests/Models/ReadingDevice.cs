using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// Deliberately implements IEquatable<T> without overriding object.Equals, so the property type's
// default equality (via IEquatable, used by the equality handler) disagrees with a boxed
// object.Equals (reference equality) for two distinct equal-content instances. Pins that correction
// detection compares with the property type's own equality, not a boxed Equals that would misread an
// equal-content source echo as divergence and synthesize corrections forever.
#pragma warning disable CA1067
public sealed class SensorReading(int value) : IEquatable<SensorReading>
{
    public int Value { get; } = value;

    public bool Equals(SensorReading? other) => other is not null && other.Value == Value;
}
#pragma warning restore CA1067

[InterceptorSubject]
public partial class ReadingDevice
{
    public partial SensorReading? Reading { get; set; }
}
