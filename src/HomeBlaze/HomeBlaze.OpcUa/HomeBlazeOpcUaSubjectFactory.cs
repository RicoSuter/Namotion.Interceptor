using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace HomeBlaze.OpcUa;

public class HomeBlazeOpcUaSubjectFactory : OpcUaSubjectFactory
{
    public HomeBlazeOpcUaSubjectFactory() : base(DefaultSubjectFactory.Instance)
    {
    }

    public override Task<IInterceptorSubject> CreateSubjectAsync(
        RegisteredSubjectProperty property, ReferenceDescription node,
        ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult<IInterceptorSubject>(new OpcUaDynamicSubject(node.BrowseName?.Name));
    }

    public override Task<IInterceptorSubject> CreateCollectionSubjectAsync(
        RegisteredSubjectProperty collectionProperty, ReferenceDescription node, object? index,
        ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult<IInterceptorSubject>(new OpcUaDynamicSubject(node.BrowseName?.Name));
    }
}
