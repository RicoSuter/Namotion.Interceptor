namespace Namotion.Interceptor.Registry.Abstractions;

public interface ISubjectRegistry : ISubjectMutationDispatcher
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects { get; }
    
    RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject);
}
