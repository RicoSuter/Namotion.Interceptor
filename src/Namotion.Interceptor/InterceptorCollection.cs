namespace Namotion.Interceptor;

public class InterceptorCollection : IInterceptorCollection
{
    private IReadInterceptor[] _readHandlers = [];
    private IWriteInterceptor[] _writeHandlers = [];

    public InterceptorCollection(
        IEnumerable<IReadInterceptor> readInterceptors, 
        IEnumerable<IWriteInterceptor> writeInterceptors)
    {
        SetHandlers(readInterceptors, writeInterceptors);
    }
    
    protected InterceptorCollection()
    {
    }

    protected void SetHandlers(IEnumerable<IReadInterceptor> readHandlers, IEnumerable<IWriteInterceptor> writeHandlers)
    {
        _readHandlers = readHandlers.Reverse().ToArray();
        _writeHandlers = writeHandlers.Reverse().ToArray();
    }

    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        var context = new ReadPropertyInterception(new PropertyReference(subject, propertyName));

        foreach (var handler in _readHandlers)
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

        foreach (var handler in _writeHandlers)
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
