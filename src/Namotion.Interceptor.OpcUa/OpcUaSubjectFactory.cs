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

    public Task<IInterceptorSubject> CreateSubjectAsync(RegisteredSubjectProperty property, ReferenceDescription node, Session session, CancellationToken cancellationToken)
    {
        var newSubject = _subjectFactory.CreateSubject(property);
        // TODO: Look up HasTypeDefinition, resolve type and create dynamic subject if mapping available
        return Task.FromResult(newSubject);
    }
}