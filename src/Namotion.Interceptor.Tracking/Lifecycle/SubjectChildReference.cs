namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A child subject found in a property's value, with the index the property holds it at.
/// </summary>
/// <param name="Subject">The child subject.</param>
/// <param name="Property">The property which holds the child subject.</param>
/// <param name="Index">The collection position or dictionary key, or null when the property holds the subject directly.</param>
public readonly record struct SubjectChildReference(IInterceptorSubject Subject, PropertyReference Property, object? Index);
