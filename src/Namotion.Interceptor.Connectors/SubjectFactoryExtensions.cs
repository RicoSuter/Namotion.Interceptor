using System.Collections;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors;

public static class SubjectFactoryExtensions
{
    public static IInterceptorSubject CreateSubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property)
    {
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(property.Type, serviceProvider);
    }

    public static IInterceptorSubject CreateCollectionSubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property, object? index)
    {
        var itemType = index is not null ?
            property.Type.IsArray ? property.Type.GetElementType() : property.Type.GenericTypeArguments[0] :
            property.Type;

        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }

    /// <summary>
    /// Creates a subject for a dictionary entry.
    /// </summary>
    /// <param name="subjectFactory">The subject factory.</param>
    /// <param name="property">The dictionary property.</param>
    /// <param name="key">The dictionary key (for context, not used in creation).</param>
    /// <returns>The created subject.</returns>
    public static IInterceptorSubject CreateDictionarySubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property, string key)
    {
        // Get the value type from the dictionary interface (IDictionary<TKey, TValue>)
        var dictionaryInterface = property.Type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        var valueType = dictionaryInterface?.GenericTypeArguments[1]
            ?? throw new InvalidOperationException("Unknown dictionary value type");

        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(valueType, serviceProvider);
    }
}
