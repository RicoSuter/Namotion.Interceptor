using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor;

public readonly record struct SubjectPropertyMetadata(
    string Name,
    Type Type,
    object[] Attributes,
    Func<object?, object?>? GetValue,
    Action<object?, object?>? SetValue)
{
    public readonly bool IsDerived => Attributes.Any(a => a is DerivedAttribute);
}
