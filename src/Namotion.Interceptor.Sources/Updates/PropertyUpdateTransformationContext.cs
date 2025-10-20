using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Updates;

/// <summary>
/// Represents the context for transforming a property update.
/// </summary>
internal struct SubjectPropertyUpdateReference
{
    public PropertyReference Property { get; }

    public IDictionary<string, SubjectPropertyUpdate> ParentCollection { get; }

    public SubjectPropertyUpdateReference(
        PropertyReference property,
        IDictionary<string, SubjectPropertyUpdate> parentCollection)
    {
        Property = property;
        ParentCollection = parentCollection;
    }
}
