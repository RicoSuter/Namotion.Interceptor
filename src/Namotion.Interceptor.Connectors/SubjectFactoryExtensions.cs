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
        Type? itemType;
        if (index is null)
        {
            itemType = property.Type;
        }
        else if (property.Type.IsArray)
        {
            itemType = property.Type.GetElementType();
        }
        else if (property.Type.GenericTypeArguments.Length == 2)
        {
            // Dictionary<TKey, TValue> - use the value type (index 1)
            itemType = property.Type.GenericTypeArguments[1];
        }
        else
        {
            // List<T>, ICollection<T>, etc. - use the element type (index 0)
            itemType = property.Type.GenericTypeArguments[0];
        }

        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }
}
