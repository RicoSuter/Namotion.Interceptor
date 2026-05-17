using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Factory for materializing subjects discovered via OPC UA browse during dynamic discovery.
/// Subclasses that override <see cref="CreateSubjectAsync"/> should usually also override
/// <see cref="CreateCollectionSubjectAsync"/>; otherwise collection elements fall back to the
/// default factory and skip the subclass customizations.
/// </summary>
public class OpcUaSubjectFactory
{
    private readonly ISubjectFactory _subjectFactory;

    public OpcUaSubjectFactory(ISubjectFactory subjectFactory)
    {
        _subjectFactory = subjectFactory;
    }

    /// <summary>
    /// Creates a subject for a single (non-collection) reference property.
    /// </summary>
    public virtual Task<IInterceptorSubject> CreateSubjectAsync(
        RegisteredSubjectProperty property, ReferenceDescription node,
        ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult(_subjectFactory.CreateSubject(property));
    }

    /// <summary>
    /// Creates a subject for an element of a collection or dictionary property.
    /// <paramref name="index"/> is the integer index for collections or the string key for dictionaries.
    /// </summary>
    public virtual Task<IInterceptorSubject> CreateCollectionSubjectAsync(
        RegisteredSubjectProperty collectionProperty, ReferenceDescription node, object? index,
        ISession session, CancellationToken cancellationToken)
    {
        return Task.FromResult(_subjectFactory.CreateCollectionSubject(collectionProperty, index));
    }
}
