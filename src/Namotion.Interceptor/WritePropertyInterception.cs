namespace Namotion.Interceptor;

public readonly record struct WritePropertyInterception(
    PropertyReference Property,
    object? CurrentValue,
    object? NewValue,
    bool IsDerived,
    IInterceptor Context)
{
    public void CallWriteProperty(object? newValue, Action<object?> writeValue, IWriteInterceptor[] writeHandlers)
    {
        for (int i = 0; i < writeHandlers.Length; i++)
        {
            var handler = writeHandlers[i];
            var previousWriteValue = writeValue;
            var copy = this;
            writeValue = (value) =>
            {
                handler.WriteProperty(copy with { NewValue = value }, ctx => previousWriteValue(ctx.NewValue));
            };
        }

        writeValue(newValue);
    }
}
