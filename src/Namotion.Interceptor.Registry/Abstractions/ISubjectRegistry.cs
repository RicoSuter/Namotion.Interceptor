namespace Namotion.Interceptor.Registry.Abstractions;

public interface ISubjectRegistry : ISubjectMutationDispatcher
{
    IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects { get; }
}
