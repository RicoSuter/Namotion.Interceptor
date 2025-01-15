namespace Namotion.Interceptor;

public class InterceptorManager : IInterceptor
{
    private IReadInterceptor[] _readHandlers = [];
    private IWriteInterceptor[] _writeHandlers = [];

    public InterceptorManager(
        IEnumerable<IReadInterceptor> readHandlers, 
        IEnumerable<IWriteInterceptor> writeHandlers)
    {
        SetHandlers(readHandlers, writeHandlers);
    }
    
    protected InterceptorManager()
    {
    }

    protected void SetHandlers(IEnumerable<IReadInterceptor> readHandlers, IEnumerable<IWriteInterceptor> writeHandlers)
    {
        _readHandlers = readHandlers.Reverse().ToArray();
        _writeHandlers = writeHandlers.Reverse().ToArray();
    }

    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        var context = new ReadPropertyInterception(new PropertyReference(subject, propertyName), this);
        return context.CallReadProperty(readValue, _readHandlers);
    }

    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var context = new WritePropertyInterception(new PropertyReference(subject, propertyName), readValue(), null, IsDerived: false);
        context.CallWriteProperty(newValue, writeValue, _writeHandlers);
    }
}
