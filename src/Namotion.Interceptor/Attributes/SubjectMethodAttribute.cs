namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Marks a method as a discoverable subject method so the source generator emits
/// it into <see cref="IInterceptorSubject.Methods"/>. Consumers can derive from
/// this attribute to add their own metadata (e.g. display titles, permissions).
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public class SubjectMethodAttribute : Attribute { }
