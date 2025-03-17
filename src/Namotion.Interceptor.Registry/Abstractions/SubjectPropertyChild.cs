namespace Namotion.Interceptor.Registry.Abstractions;

public readonly record struct SubjectPropertyChild
{
    public IInterceptorSubject Subject { get; init; }

    /// <summary>
    /// Specifies the index of the subject in the parent's property collection or dictionary (null denotes a direct subject reference).
    /// </summary>
    public object? Index { get; init; }
}