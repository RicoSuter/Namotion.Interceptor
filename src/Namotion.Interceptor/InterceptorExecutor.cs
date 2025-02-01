namespace Namotion.Interceptor;

public readonly struct InterceptorExecutor : IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context = new InterceptorSubjectContext();

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        return _context.TryAddService(factory, exists);
    }

    public bool HasChangedSince(DateTimeOffset time)
    {
        return _context.HasChangedSince(time);
    }

    public void AddService<TService>(TService service)
    {
        _context.AddService(service);
    }

    public TInterface? TryGetService<TInterface>()
    {
        return _context.TryGetService<TInterface>();
    }

    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        return _context.GetServices<TInterface>();
    }
    
    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        var readInterceptors = subject.Context.GetServices<IReadInterceptor>();
        var interception = new ReadPropertyInterception(new PropertyReference(subject, propertyName));
        
        var returnReadValue = new Func<ReadPropertyInterception, object?>(_ => readValue());
    
        foreach (var handler in readInterceptors)
        {
            var previousReadValue = returnReadValue;
            returnReadValue = context =>
                handler.ReadProperty(context, ctx => previousReadValue(ctx));
        }

        return returnReadValue(interception);
    }
    
    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var writeInterceptors = subject.Context.GetServices<IWriteInterceptor>();
        var interception = new WritePropertyInterception(new PropertyReference(subject, propertyName), readValue(), newValue);

        var returnWriteValue = new Func<WritePropertyInterception, object?>(value =>
        {
            writeValue(value.NewValue);
            return value.NewValue;
        });
    
        foreach (var handler in writeInterceptors)
        {
            var previousWriteValue = returnWriteValue;
            returnWriteValue = (context) =>
            {
                return handler.WriteProperty(context,
                    innerContext => previousWriteValue(innerContext));
            };
        }

        returnWriteValue(interception);
    }

    public void AddFallbackContext(IInterceptorSubjectContext context)
    {
        _context.AddFallbackContext(context);
        
        foreach (var interceptor in context.GetServices<IInterceptor>())
        {
            interceptor.AttachTo(_subject);
        }
    }

    public void RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        foreach (var interceptor in context.GetServices<IInterceptor>())
        {
            interceptor.DetachFrom(_subject);
        }
        
        _context.RemoveFallbackContext(context);
    }
}