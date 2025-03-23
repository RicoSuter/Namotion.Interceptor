using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

public interface ISubjectFactory
{
    IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, object? index);

    ICollection<IInterceptorSubject?> CreateSubjectCollection(RegisteredSubjectProperty property, params IEnumerable<IInterceptorSubject?> children);
}