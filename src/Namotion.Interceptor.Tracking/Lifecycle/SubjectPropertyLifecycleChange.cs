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
    internal PropertyAttachDeferral? Deferral { get; init; }
}