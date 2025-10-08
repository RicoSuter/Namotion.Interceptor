using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Updates;

public interface ISubjectUpdateProcessor
{
    bool IsIncluded(RegisteredSubjectProperty property);
    
    SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update);

    SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update);
}