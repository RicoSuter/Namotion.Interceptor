using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Performance;

namespace Namotion.Interceptor.Sources.Updates.Performance;

internal static class SubjectUpdatePools
{
    private static readonly ObjectPool<Dictionary<IInterceptorSubject, SubjectUpdate>> KnownSubjectUpdatesPool
        = new(() => new Dictionary<IInterceptorSubject, SubjectUpdate>());

    private static readonly ObjectPool<Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>> PropertyUpdatesPool
        = new(() => new Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>());

    private static readonly ObjectPool<HashSet<IInterceptorSubject>> ProcessedParentPathsPool
        = new(() => []);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<IInterceptorSubject, SubjectUpdate> RentKnownSubjectUpdates()
    {
        return KnownSubjectUpdatesPool.Rent();
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
        return PropertyUpdatesPool.Rent();
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
        return ProcessedParentPathsPool.Rent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnProcessedParentPaths(HashSet<IInterceptorSubject> hashSet)
    {
        hashSet.Clear();
        ProcessedParentPathsPool.Return(hashSet);
    }
}