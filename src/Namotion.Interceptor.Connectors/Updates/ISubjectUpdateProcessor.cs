using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

public interface ISubjectUpdateProcessor
{
    public bool IsIncluded(RegisteredSubjectProperty property) => true;

    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update) => update;

    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update) => update;
}
