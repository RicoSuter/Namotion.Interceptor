namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A subject held by a structural property, with its place in the property's value.
/// </summary>
/// <param name="Subject">The subject.</param>
/// <param name="Index">
/// The key or position of the subject in the value: the first one where the value holds the subject
/// more than once, and <c>null</c> where the value is the subject itself.
/// </param>
public readonly record struct SubjectChild(IInterceptorSubject Subject, object? Index);
