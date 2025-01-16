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
        var context = new ReadPropertyInterception(new PropertyReference(subject, propertyName));

        foreach (var handler in _readInterceptors)
        {
            var previousReadValue = readValue;
            var contextCopy = context;
            readValue = () =>
            {
                return handler.ReadProperty(contextCopy, _ => previousReadValue());
            };
        }
        
        return readValue.Invoke();
    }

    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var context = new WritePropertyInterception(new PropertyReference(subject, propertyName), readValue(), null, IsDerived: false);

        foreach (var handler in _writeInterceptors)
        {
            var previousWriteValue = writeValue;
            var contextCopy = context;
            writeValue = (value) =>
            {
                handler.WriteProperty(contextCopy with { NewValue = value }, ctx => previousWriteValue(ctx.NewValue));
            };
        }

        writeValue(newValue);
    }
}
