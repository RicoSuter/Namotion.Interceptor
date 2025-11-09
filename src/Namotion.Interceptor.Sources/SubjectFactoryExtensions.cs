using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources;

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
}