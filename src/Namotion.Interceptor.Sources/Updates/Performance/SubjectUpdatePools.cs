namespace Namotion.Interceptor.Sources.Updates.Performance;

internal static class SubjectUpdatePools
{
    private sealed class SimpleObjectPool<T> where T : class
    {
        private volatile T?[] _items;
        private readonly Func<T> _factory;
        private readonly Action<T>? _reset;
        private readonly Lock _resizeLock = new();

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
                    return inst;
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
                    return;
            }

            lock (_resizeLock)
            {
                items = _items;
                for (var i = 0; i < items.Length; i++)
                {
                    if (Interlocked.CompareExchange(ref items[i], item, null) is null)
                        return;
                }

                var newSize = items.Length * 2;
                if (newSize <= items.Length)
                    newSize = items.Length + 1;

                var newArray = new T?[newSize];
                for (var i = 0; i < items.Length; i++)
                    newArray[i] = items[i];

                for (var i = 0; i < newArray.Length; i++)
                {
                    if (newArray[i] is null)
                    {
                        newArray[i] = item;
                        break;
                    }
                }

                _items = newArray;
            }
        }
    }

    private const int PoolSize = 16;

    private static readonly SimpleObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool
        = new(PoolSize, () => new Dictionary<IInterceptorSubject, SubjectUpdate>(), d => d.Clear());

    private static readonly SimpleObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>> PropertyUpdatesPool
        = new(PoolSize, () => new Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>(), d => d.Clear());

    private static readonly SimpleObjectPool<HashSet<IInterceptorSubject>> ProcessedParentPathsPool
        = new(PoolSize, () => new HashSet<IInterceptorSubject>(), s => s.Clear());

    public static Dictionary<IInterceptorSubject, SubjectUpdate> RentKnownSubjectUpdates()
        => KnownSubjectUpdatesPool.Rent();

    public static void ReturnKnownSubjectUpdates(Dictionary<IInterceptorSubject, SubjectUpdate> d)
        => KnownSubjectUpdatesPool.Return(d);

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
        => ProcessedParentPathsPool.Return(s);
}
