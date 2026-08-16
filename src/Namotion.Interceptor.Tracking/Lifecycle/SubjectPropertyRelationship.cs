namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// An immutable occurrence of a child subject in a parent property.
/// </summary>
public sealed class SubjectPropertyRelationship
{
    internal SubjectPropertyRelationship(PropertyReference parent, IInterceptorSubject child, object? index)
    {
        Parent = parent;
        Child = child;
        Index = index;
    }

    /// <summary>
    /// Gets the parent property that holds the child occurrence.
    /// </summary>
    public PropertyReference Parent { get; }

    /// <summary>
    /// Gets the child subject held by the parent property.
    /// </summary>
    public IInterceptorSubject Child { get; }

    /// <summary>
    /// Gets the collection position or dictionary key, or null when the property holds the child directly.
    /// </summary>
    public object? Index { get; }
}
