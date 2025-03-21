namespace Namotion.Interceptor.Registry.Abstractions;

public interface ISubjectRegistry
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects { get; }

    RegisteredSubject RegisterSubject(IInterceptorSubject subject);
}
