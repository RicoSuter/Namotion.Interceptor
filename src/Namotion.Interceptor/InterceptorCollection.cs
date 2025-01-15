namespace Namotion.Interceptor;

public class InterceptorCollection : IInterceptor
{
    private IReadInterceptor[] _readHandlers = [];
    private IWriteInterceptor[] _writeHandlers = [];

    public InterceptorCollection(
        IEnumerable<IReadInterceptor> readHandlers, 
        IEnumerable<IWriteInterceptor> writeHandlers)
    {
        SetHandlers(readHandlers, writeHandlers);
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
            var copy = context;
            readValue = () =>
            {
                return handler.ReadProperty(copy, _ => previousReadValue());
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
            var copy = context;
            writeValue = (value) =>
            {
                handler.WriteProperty(copy with { NewValue = value }, ctx => previousWriteValue(ctx.NewValue));
            };
        }

        writeValue(newValue);
    }
}
