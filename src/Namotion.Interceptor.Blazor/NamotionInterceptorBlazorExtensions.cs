using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class NamotionInterceptorBlazorExtensions
{
    public static IServiceCollection AddInterceptorSubjectRoot<TRoot>(this IServiceCollection serviceCollection) 
        where TRoot : class
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithReadPropertyRecorder();

        return serviceCollection
            .AddSingleton<TRoot>(sp => ActivatorUtilities.CreateInstance<TRoot>(sp, context));
    }
}