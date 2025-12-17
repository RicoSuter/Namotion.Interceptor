using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents the context for transforming a property update.
/// </summary>
internal struct SubjectPropertyUpdateReference
{
    public RegisteredSubjectProperty Property { get; }

    public IDictionary<string, SubjectPropertyUpdate> ParentCollection { get; }

    public SubjectPropertyUpdateReference(
        RegisteredSubjectProperty property,
        IDictionary<string, SubjectPropertyUpdate> parentCollection)
    {
        Property = property;
        ParentCollection = parentCollection;
    }
}
