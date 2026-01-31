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

    public virtual Task<IInterceptorSubject> CreateSubjectForPropertyAsync(
        RegisteredSubjectProperty property, ReferenceDescription node,
        ISession session, CancellationToken cancellationToken)
    {
        // For collection/dictionary properties, create an item using the element type
        // (not the collection type itself which cannot be instantiated)
        if (property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            return Task.FromResult(_subjectFactory.CreateSubjectForCollectionOrDictionaryProperty(property));
        }

        return Task.FromResult(_subjectFactory.CreateSubjectForReferenceProperty(property));
    }
}