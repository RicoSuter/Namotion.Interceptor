using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public interface ISubjectFactory
{
    IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, object? index);
}