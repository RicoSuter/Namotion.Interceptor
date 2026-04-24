using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// Minimal sealed marker subclass of <see cref="RegisteredSubjectProperty"/> used to
/// test whether the subclass's existence in the FrozenDictionary values causes
/// measurable GC or JIT regressions. No new fields; same instance size as Property.
/// </summary>
public sealed class RegisteredSubjectAttribute : RegisteredSubjectProperty
{
    internal RegisteredSubjectAttribute(
        RegisteredSubject parent, string name, Type type,
        IReadOnlyCollection<Attribute> reflectionAttributes)
        : base(parent, name, type, reflectionAttributes)
    {
    }
}
