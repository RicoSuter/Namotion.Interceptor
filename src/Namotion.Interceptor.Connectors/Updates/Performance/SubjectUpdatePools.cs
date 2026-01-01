using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Namotion.Interceptor.Connectors.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Performance;

internal static class SubjectUpdatePools
{
    private const int PoolMaxSize = 256;

    private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool =
        new DefaultObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>>(
            new DictionaryPoolPolicy<IInterceptorSubject, SubjectUpdate>(), PoolMaxSize);

    private static readonly ObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>> PropertyUpdatesPool =
        new DefaultObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>>(
            new DictionaryPoolPolicy<SubjectPropertyUpdate, SubjectPropertyUpdateReference>(), PoolMaxSize);

    private static readonly ObjectPool<HashSet<IInterceptorSubject>> ProcessedParentPathsPool =
        new DefaultObjectPool<HashSet<IInterceptorSubject>>(
            new HashSetPoolPolicy<IInterceptorSubject>(), PoolMaxSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<IInterceptorSubject, SubjectUpdate> RentKnownSubjectUpdates()
    {
        return KnownSubjectUpdatesPool.Get();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnKnownSubjectUpdates(Dictionary<IInterceptorSubject, SubjectUpdate> dictionary)
    {
        dictionary.Clear();
        KnownSubjectUpdatesPool.Return(dictionary);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference> RentPropertyUpdates()
    {
        return PropertyUpdatesPool.Get();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnPropertyUpdates(Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? dictionary)
    {
        if (dictionary is not null)
        {
            dictionary.Clear();
            PropertyUpdatesPool.Return(dictionary);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HashSet<IInterceptorSubject> RentProcessedParentPaths()
    {
        return ProcessedParentPathsPool.Get();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnProcessedParentPaths(HashSet<IInterceptorSubject> hashSet)
    {
        hashSet.Clear();
        ProcessedParentPathsPool.Return(hashSet);
    }
}
