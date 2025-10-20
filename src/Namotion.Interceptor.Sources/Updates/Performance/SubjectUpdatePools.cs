namespace Namotion.Interceptor.Sources.Updates.Performance;

internal static class SubjectUpdatePools
{
    // A very small, fast object pool suitable for pooling reference types used during update creation.
    // It now supports growing (doubling) when the pool is full. Resizing is done under a lock so Rent remains
    // lock-free and fast in the common case.
    private sealed class SimpleObjectPool<T> where T : class
    {
        private volatile T?[] _items;
        private readonly Func<T> _factory;
        private readonly Action<T>? _reset;
        private readonly object _resizeLock = new();

        public SimpleObjectPool(int size, Func<T> factory, Action<T>? reset = null)
        {
            _items = new T?[size];
            _factory = factory;
            _reset = reset;
        }

        public T Rent()
        {
            var items = _items;
            for (var i = 0; i < items.Length; i++)
            {
                var inst = Interlocked.Exchange(ref items[i], null);
                if (inst is not null)
                {
                    return inst;
                }
            }

            return _factory();
        }

        public void Return(T item)
        {
            _reset?.Invoke(item);

            var items = _items;
            for (var i = 0; i < items.Length; i++)
            {
                if (Interlocked.CompareExchange(ref items[i], item, null) is null)
                {
                    return;
                }
            }

            // No free slot found; grow the pool under a lock (only one thread will resize at a time).
            lock (_resizeLock)
            {
                items = _items; // refresh reference in case another thread already resized

                // Try again once after taking the lock in case another thread freed a slot
                for (var i = 0; i < items.Length; i++)
                {
                    if (Interlocked.CompareExchange(ref items[i], item, null) is null)
                    {
                        return;
                    }
                }

                // Still full -> allocate a new array with double the size and copy existing entries.
                var newSize = items.Length * 2;
                if (newSize <= items.Length) // overflow guard
                {
                    newSize = items.Length + 1;
                }

                var newArray = new T?[newSize];
                for (var i = 0; i < items.Length; i++)
                {
                    newArray[i] = items[i];
                }

                // Place the returned item in the first free slot in the new array.
                for (var i = 0; i < newArray.Length; i++)
                {
                    if (newArray[i] is null)
                    {
                        newArray[i] = item;
                        break;
                    }
                }

                // Publish the new array for future Rent/Return calls.
                _items = newArray;
            }
        }
    }

    private const int PoolSize = 16; // initial pool size per type

    private static readonly SimpleObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool
        = new(PoolSize, () => new Dictionary<IInterceptorSubject, SubjectUpdate>(), d => d.Clear());

    private static readonly SimpleObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>> PropertyUpdatesPool
        = new(PoolSize, () => new Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>(), d => d.Clear());

    private static readonly SimpleObjectPool<HashSet<IInterceptorSubject>> ProcessedParentPathsPool
        = new(PoolSize, () => new HashSet<IInterceptorSubject>(), s => s.Clear());

    public static Dictionary<IInterceptorSubject, SubjectUpdate> RentKnownSubjectUpdates()
        => KnownSubjectUpdatesPool.Rent();

    public static void ReturnKnownSubjectUpdates(Dictionary<IInterceptorSubject, SubjectUpdate> d)
    {
        KnownSubjectUpdatesPool.Return(d);
    }

    public static Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference> RentPropertyUpdates()
        => PropertyUpdatesPool.Rent();

    public static void ReturnPropertyUpdates(Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? d)
    {
        if (d is not null)
            PropertyUpdatesPool.Return(d);
    }

    public static HashSet<IInterceptorSubject> RentProcessedParentPaths()
        => ProcessedParentPathsPool.Rent();

    public static void ReturnProcessedParentPaths(HashSet<IInterceptorSubject> s)
    {
        ProcessedParentPathsPool.Return(s);
    }
}
