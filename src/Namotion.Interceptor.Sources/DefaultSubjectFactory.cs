using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Uses reflection and optional <see cref="IServiceProvider"/> to create subjects and subject collections.
/// </summary>
public class DefaultSubjectFactory : ISubjectFactory
{
    public static DefaultSubjectFactory Instance { get; } = new();

    /// <inheritdoc />
    public virtual IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, object? index)
    {
        var itemType = index is not null ? property.Type.GenericTypeArguments[0] : property.Type;
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        var item = (serviceProvider is not null
                ? ActivatorUtilities.CreateInstance(serviceProvider, itemType) as IInterceptorSubject
                : Activator.CreateInstance(itemType) as IInterceptorSubject)
            ?? throw new InvalidOperationException("Could not create subject.");
        
        return item;
    }

    /// <inheritdoc />
    public IEnumerable<IInterceptorSubject?> CreateSubjectCollection(RegisteredSubjectProperty property, params IEnumerable<IInterceptorSubject?> children)
    {
        if (property.Type.IsArray)
        {
            var childSubjectList = new List<IInterceptorSubject?>(children);
            var array = Array.CreateInstance(property.Type.GetElementType()!, childSubjectList.Count);
            for (var arrayIndex = 0; arrayIndex < childSubjectList.Count; arrayIndex++)
            {
                array.SetValue(childSubjectList[arrayIndex], arrayIndex);
            }

            return (IEnumerable<IInterceptorSubject?>)array;
        }
        
        var itemType = property.Type.GenericTypeArguments[0];
        var collectionType = typeof(List<>).MakeGenericType(itemType);

        var collection = (IList)Activator.CreateInstance(collectionType)!;
        foreach (var subject in children)
        {
            collection.Add(subject);
        }

        return (IEnumerable<IInterceptorSubject?>)collection;
    }
}