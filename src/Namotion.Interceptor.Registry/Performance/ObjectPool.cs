using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Registry.Performance;

public sealed class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> _objects;
    private readonly Func<T> _factory;

    public ObjectPool(Func<T> factory)
    {
        _objects = [];
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        return _objects.TryTake(out var item) ? item : _factory();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item)
    {
        _objects.Add(item);
    }
}
