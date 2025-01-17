namespace Namotion.Interceptor;

public readonly struct InterceptorCollection : IInterceptorCollection
{
    private readonly IInterceptorSubject _subject;
    private readonly List<IReadInterceptor> _readInterceptors = [];
    private readonly List<IWriteInterceptor> _writeInterceptors = [];

    public InterceptorCollection(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public IEnumerable<IInterceptor> Interceptors => _readInterceptors
        .OfType<IInterceptor>()
        .Union(_writeInterceptors);

    public void AddInterceptor(IInterceptor interceptor)
    {
        if (interceptor is IReadInterceptor readInterceptor)
            _readInterceptors.Add(readInterceptor);

        if (interceptor is IWriteInterceptor writeInterceptor)
            _writeInterceptors.Add(writeInterceptor);

        interceptor.AttachTo(_subject);
    }

    public void RemoveInterceptor(IInterceptor interceptor)
    {
        if (interceptor is IReadInterceptor readInterceptor)
            _readInterceptors.Remove(readInterceptor);

        if (interceptor is IWriteInterceptor writeInterceptor)
            _writeInterceptors.Remove(writeInterceptor);

        interceptor.DetachFrom(_subject);
    }

    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        // TODO: Improve performance by caching here

        var returnReadValue = new Func<ReadPropertyInterception, object?>(_ => readValue());

        foreach (var handler in _readInterceptors)
        {
            var previousReadValue = returnReadValue;
            returnReadValue = context => 
                handler.ReadProperty(context, ctx => previousReadValue(ctx));
        }

        var context = new ReadPropertyInterception(new PropertyReference(subject, propertyName));
        return returnReadValue.Invoke(context);
    }

    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        // TODO: Improve performance by caching here
        
        var returnWriteValue = new Func<WritePropertyInterception, object?>(value =>
        {
            writeValue(value.NewValue);
            return value;
        });

        foreach (var handler in _writeInterceptors)
        {
            var previousWriteValue = returnWriteValue;
            returnWriteValue = (context) =>
            {
                return handler.WriteProperty(context,
                    innerContext => previousWriteValue(innerContext));
            };
        }

        var context = new WritePropertyInterception(new PropertyReference(subject, propertyName), readValue(), newValue);
        returnWriteValue(context);
    }
}