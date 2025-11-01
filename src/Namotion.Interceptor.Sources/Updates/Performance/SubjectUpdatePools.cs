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
    public static void ReturnKnownSubjectUpdates(Dictionary<IInterceptorSubject, SubjectUpdate> d)
    {
        d.Clear();
        KnownSubjectUpdatesPool.Return(d);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference> RentPropertyUpdates()
    {
        return PropertyUpdatesPool.Rent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnPropertyUpdates(Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? d)
    {
        if (d is not null)
        {
            d.Clear();
            PropertyUpdatesPool.Return(d);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HashSet<IInterceptorSubject> RentProcessedParentPaths()
    {
        return ProcessedParentPathsPool.Rent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnProcessedParentPaths(HashSet<IInterceptorSubject> s)
    {
        s.Clear();
        ProcessedParentPathsPool.Return(s);
    }
}
