using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public class DefaultSubjectFactory : ISubjectFactory
{
    public static DefaultSubjectFactory Instance { get; } = new();
    
    public IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, object? index)
    {
        var itemType = index is not null ? property.Type.GenericTypeArguments[0] : property.Type;
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        var item = serviceProvider is not null
            ? ActivatorUtilities.CreateInstance(serviceProvider, itemType, [])
            : Activator.CreateInstance(itemType);
        
        return item as IInterceptorSubject ?? throw new InvalidOperationException("Could not create subject.");
    }
}