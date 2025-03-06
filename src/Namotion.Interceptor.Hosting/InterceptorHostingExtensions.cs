using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorHostingExtensions
{
    private const string AttachedHostedServicesKey = "Namotion.Hosting.AttachedHostedServices";

    public static void AttachHostedService(this IInterceptorSubject subject, IHostedService hostedService)
    {
        subject.Data.AddOrUpdate(AttachedHostedServicesKey, 
            _ =>
            {
                HandleAttachHostedService(subject, hostedService);
                return new HashSet<IHostedService> { hostedService };
            },
            (_, value) =>
            {
                var hashSet = value as HashSet<IHostedService>;
                if (hashSet?.Add(hostedService) == true)
                {
                    HandleAttachHostedService(subject, hostedService);
                }
                return hashSet;
            });
    }

    public static void DetachHostedService(this IInterceptorSubject subject, IHostedService hostedService)
    {
        subject.Data.AddOrUpdate(AttachedHostedServicesKey, 
            _ => new HashSet<IHostedService>(),
            (_, value) =>
            {
                var hashSet = value as HashSet<IHostedService>;
                if (hashSet?.Remove(hostedService) == true)
                {
                    HandleDetachHostedService(subject, hostedService);
                }
                return hashSet?.Count > 0 ? hashSet : null;
            });
    }

    private static void HandleAttachHostedService(IInterceptorSubject subject, IHostedService hostedService)
    {
        var hostedServiceHandler = subject.Context.TryGetService<HostedServiceHandler>();
        hostedServiceHandler?.AttachHostedService(hostedService);
    }

    private static void HandleDetachHostedService(IInterceptorSubject subject, IHostedService hostedService)
    {
        var hostedServiceHandler = subject.Context.TryGetService<HostedServiceHandler>();
        hostedServiceHandler?.DetachHostedService(hostedService);
    }

    public static HashSet<IHostedService>? TryGetAttachedHostedServices(this IInterceptorSubject subject)
    {
        return subject.Data.GetOrAdd(AttachedHostedServicesKey, _ => null) as HashSet<IHostedService>;
    }
}
