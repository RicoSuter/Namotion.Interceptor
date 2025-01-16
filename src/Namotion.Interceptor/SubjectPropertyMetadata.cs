namespace Namotion.Interceptor;

public readonly record struct SubjectPropertyMetadata(
    string Name,
    Type Type,
    object[] Attributes,
    Func<object?, object?>? GetValue,
    Action<object?, object?>? SetValue)
{
}
