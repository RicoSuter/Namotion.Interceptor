using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Registry.Performance;

/// <summary>
/// A simple thread-safe object pool using ConcurrentBag.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
public sealed class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> _objects = [];
    private readonly Func<T> _factory;

    public ObjectPool(Func<T> factory)
    {
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
