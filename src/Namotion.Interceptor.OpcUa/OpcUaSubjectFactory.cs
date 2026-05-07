using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
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

    public virtual Task<IInterceptorSubject> CreateSubjectAsync(
        RegisteredSubjectProperty property, ReferenceDescription node,
        ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult(_subjectFactory.CreateSubject(property));
    }

    public virtual Task<IInterceptorSubject> CreateCollectionSubjectAsync(
        RegisteredSubjectProperty collectionProperty, ReferenceDescription node, object? index,
        ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult(_subjectFactory.CreateCollectionSubject(collectionProperty, index));
    }
}