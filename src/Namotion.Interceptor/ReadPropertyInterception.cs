namespace Namotion.Interceptor;

public readonly record struct ReadPropertyInterception(
    PropertyReference Property,
    IInterceptor Context)
{
    public object? CallReadProperty(Func<object?> readValue, IReadInterceptor[] readInterceptors)
    {
        for (int i = 0; i < readInterceptors.Length; i++)
        {
            var handler = readInterceptors[i];
            var previousReadValue = readValue;
            var copy = this;
            readValue = () =>
            {
                return handler.ReadProperty(copy, _ => previousReadValue());
            };
        }
        
        return readValue.Invoke();    
    }
}
