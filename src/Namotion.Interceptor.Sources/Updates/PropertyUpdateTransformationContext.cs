using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Updates;

/// <summary>
/// Represents the context for transforming a property update.
/// </summary>
internal struct SubjectPropertyUpdateReference
{
    public RegisteredSubjectProperty Property { get; }
    public SubjectPropertyUpdate Update { get; }
    public IDictionary<string, SubjectPropertyUpdate> ParentCollection { get; }

    public SubjectPropertyUpdateReference(
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate update,
        IDictionary<string, SubjectPropertyUpdate> parentCollection)
    {
        Property = property;
        Update = update;
        ParentCollection = parentCollection;
    }
}
