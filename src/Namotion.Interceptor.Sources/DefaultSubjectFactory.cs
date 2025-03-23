using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Uses reflection and optional <see cref="IServiceProvider"/> to create subjects and subject collections.
/// </summary>
public class DefaultSubjectFactory : ISubjectFactory
{
    public static DefaultSubjectFactory Instance { get; } = new();

    public virtual IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, object? index)
    {
        var itemType = index is not null ? property.Type.GenericTypeArguments[0] : property.Type;
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        var item = serviceProvider is not null
            ? ActivatorUtilities.CreateInstance(serviceProvider, itemType, [])
            : Activator.CreateInstance(itemType);

        return item as IInterceptorSubject ?? throw new InvalidOperationException("Could not create subject.");
    }

    public ICollection<IInterceptorSubject?> CreateSubjectCollection(RegisteredSubjectProperty property, params IEnumerable<IInterceptorSubject?> children)
    {
        var itemType = property.Type.GenericTypeArguments[0];
        var collectionType = typeof(List<>).MakeGenericType(itemType);
     
        var collection = (ICollection<IInterceptorSubject?>)Activator.CreateInstance(collectionType)!;
        foreach (var subject in children)
        {
            collection.Add(subject);
        }

        return collection;
    }
}