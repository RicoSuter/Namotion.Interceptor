namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <param name="Subject">Gets the subject where a property reference pointing to it has been changed.</param>
/// <param name="Property">Gets the property.</param>
public record struct SubjectPropertyLifecycleChange(
    IInterceptorSubject Subject,
    PropertyReference Property)
{
    /// <summary>
    /// Set while a <see cref="LifecycleInterceptor"/> attaches the properties of subjects entering the graph.
    /// </summary>
    internal AttachOperation? AttachOperation { get; init; }

    // Excludes AttachOperation, which says how the change was raised rather than what changed.
    public readonly bool Equals(SubjectPropertyLifecycleChange other)
    {
        return Equals(Subject, other.Subject) && Property.Equals(other.Property);
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(Subject, Property);
    }
}