using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// One <c>AddSubject</c> or <c>AddKeyedSubject</c> call: how to construct the subject, which context
/// it joins, and the private context host when it gets one.
/// </summary>
internal sealed class SubjectRegistration<T>
    where T : class, IInterceptorSubject
{
    private readonly Action<T>? _configure;
    private readonly Func<IServiceProvider, IInterceptorSubjectContext>? _contextResolver;
    private readonly ObjectFactory? _contextFactory;

    public SubjectRegistration(
        object? serviceKey,
        Action<T>? configure,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver,
        ObjectFactory? contextFactory)
    {
        ServiceKey = serviceKey;
        _configure = configure;
        _contextResolver = contextResolver;
        _contextFactory = contextFactory;
    }

    public object? ServiceKey { get; }

    public bool UsesPrivateContext => _contextResolver is null;

    /// <summary>
    /// Every instance this registration constructed, with its private context host, or null in shared
    /// mode. Per instance rather than one field, because every provider built from the collection runs
    /// the factory for its own instance.
    /// </summary>
    private readonly ConditionalWeakTable<T, PrivateContextHost?> _createdInstances = new();

    /// <summary>
    /// Whether this registration constructed <paramref name="instance"/>, rather than the caller
    /// registering it, and its private context host, which is null in shared mode.
    /// </summary>
    public bool TryGetCreatedInstance(T instance, out PrivateContextHost? host)
        => _createdInstances.TryGetValue(instance, out host);

    public T Resolve(IServiceProvider serviceProvider)
        => ServiceKey is null
            ? serviceProvider.GetRequiredService<T>()
            : serviceProvider.GetRequiredKeyedService<T>(ServiceKey);

    /// <summary>
    /// Constructs, configures and attaches the subject, so it is fully configured before anything can
    /// start it, whichever constructor shape it has.
    /// </summary>
    public T Create(IServiceProvider serviceProvider)
    {
        if (_contextResolver is null)
        {
            // Constructed detached, so the private context is complete before the subject joins it. The
            // construction context has no interceptors, so a constructor that attaches to it raises no
            // lifecycle events, and it is never one registered in dependency injection.
            var constructionContext = InterceptorSubjectContext.Create();
            var instance = Construct(serviceProvider, constructionContext);
            instance.Context.RemoveFallbackContext(constructionContext);

            // Attached at resolution, so the subject is in its context from then on. Nothing starts
            // until SubjectActivation<T> opens the host's handler.
            var host = new PrivateContextHost(serviceProvider);
            host.Attach(instance);
            _createdInstances.Add(instance, host);

            // Handed to this provider's activation now rather than when it starts: an awaited attach
            // can open the host before host start, and the activation's disposal is what still stops it
            // when that start never comes.
            serviceProvider.GetRequiredKeyedService<SubjectActivation<T>>(this).RecordHost(host);
            return instance;
        }

        var context = _contextResolver(serviceProvider)
            ?? throw new InvalidOperationException($"The context resolver for {typeof(T).Name} returned null.");

        // The shared context's handler may already be running, so the start deferral holds back any
        // start until configure has run, whichever constructor shape attaches the subject.
        using (context.DeferHostedServiceStarts())
        {
            var sharedInstance = Construct(serviceProvider, context);

            // Also for the shape that takes the context and ignores it, which is otherwise unattached.
            sharedInstance.Context.AddFallbackContext(context);
            _createdInstances.Add(sharedInstance, null);
            return sharedInstance;
        }
    }

    private T Construct(IServiceProvider serviceProvider, IInterceptorSubjectContext context)
    {
        // The factory is the decision, not a reflection query: reflection answers the looser question
        // of whether a constructor mentions the type, not whether it can be called with it.
        var instance = _contextFactory is not null
            ? (T)_contextFactory(serviceProvider, [context])
            : ActivatorUtilities.CreateInstance<T>(serviceProvider);

        _configure?.Invoke(instance);
        return instance;
    }
}
