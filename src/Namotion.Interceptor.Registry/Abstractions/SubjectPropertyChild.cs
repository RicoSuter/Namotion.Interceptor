namespace Namotion.Interceptor.Registry.Abstractions;

public readonly record struct SubjectPropertyChild
{
    public IInterceptorSubject Subject { get; init; }

    public object? Index { get; init; }
}