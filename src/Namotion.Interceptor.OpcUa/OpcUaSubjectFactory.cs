using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa;

public class OpcUaSubjectFactory
{
    private readonly ISubjectFactory _subjectFactory;

    public OpcUaSubjectFactory(ISubjectFactory subjectFactory)
    {
        _subjectFactory = subjectFactory;
    }

    public virtual Task<IInterceptorSubject> CreateSubjectAsync(RegisteredSubjectProperty property, ReferenceDescription node, ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult(_subjectFactory.CreateSubject(property));
    }
}