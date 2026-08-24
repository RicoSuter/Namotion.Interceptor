using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Thread-static scratch pools for the lifecycle's traversals. Every graph operation needs a handful
/// of short-lived lists, sets and stacks, and reentrancy (a release descent re-entered from a
/// removal callback, or an admission from a lifecycle callback) means several can be live on one
/// thread at once, so they are pooled rather than held as fields.
///
/// The pools are unbounded and never trimmed, so a thread retains buffers sized to the largest
/// graph operation it ever performed. Occurrence and index buffers are bounded by the widest single
/// structural value, but discovery, release and reachability state is not: a component discovery
/// visits every newly reachable subject, and a reachability walk visits an ancestor closure, so
/// those buffers can reach the size of a whole component. Discarding oversized buffers on return
/// would cap that at the price of reallocating on every large operation; that trade has not been
/// measured, so the high-water mark is retained.
///
/// Subject-keyed sets and maps use reference equality; see <see cref="OwnershipGraph"/> for why
/// graph membership is identity.
/// </summary>
internal static class LifecycleScratch
{
    [ThreadStatic]
    private static Stack<List<SubjectOccurrence>>? _occurrenceLists;

    [ThreadStatic]
    private static Stack<List<IncomingEdge>>? _edgeLists;

    [ThreadStatic]
    private static Stack<List<object?>>? _indexLists;

    [ThreadStatic]
    private static Stack<List<IInterceptorSubject>>? _subjectLists;

    [ThreadStatic]
    private static Stack<HashSet<IInterceptorSubject>>? _subjectSets;

    [ThreadStatic]
    private static Stack<Stack<IInterceptorSubject>>? _subjectStacks;

    [ThreadStatic]
    private static Stack<Dictionary<IInterceptorSubject, int>>? _subjectCounters;

    [ThreadStatic]
    private static Stack<Dictionary<IInterceptorSubject, List<object?>>>? _indexGroups;

    [ThreadStatic]
    private static Stack<List<(PropertyReference Property, SubjectOccurrence Occurrence)>>? _childLists;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<SubjectOccurrence> RentOccurrenceList()
    {
        _occurrenceLists ??= new Stack<List<SubjectOccurrence>>();
        return _occurrenceLists.Count > 0 ? _occurrenceLists.Pop() : new List<SubjectOccurrence>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(List<SubjectOccurrence> list)
    {
        list.Clear();
        _occurrenceLists ??= new Stack<List<SubjectOccurrence>>();
        _occurrenceLists.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<IncomingEdge> RentEdgeList()
    {
        _edgeLists ??= new Stack<List<IncomingEdge>>();
        return _edgeLists.Count > 0 ? _edgeLists.Pop() : new List<IncomingEdge>(4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(List<IncomingEdge> list)
    {
        list.Clear();
        _edgeLists ??= new Stack<List<IncomingEdge>>();
        _edgeLists.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<object?> RentIndexList()
    {
        _indexLists ??= new Stack<List<object?>>();
        return _indexLists.Count > 0 ? _indexLists.Pop() : new List<object?>(4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(List<object?> list)
    {
        list.Clear();
        _indexLists ??= new Stack<List<object?>>();
        _indexLists.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<IInterceptorSubject> RentSubjectList()
    {
        _subjectLists ??= new Stack<List<IInterceptorSubject>>();
        return _subjectLists.Count > 0 ? _subjectLists.Pop() : new List<IInterceptorSubject>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(List<IInterceptorSubject> list)
    {
        list.Clear();
        _subjectLists ??= new Stack<List<IInterceptorSubject>>();
        _subjectLists.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HashSet<IInterceptorSubject> RentSubjectSet()
    {
        _subjectSets ??= new Stack<HashSet<IInterceptorSubject>>();
        return _subjectSets.Count > 0 ? _subjectSets.Pop() : new HashSet<IInterceptorSubject>(8, ReferenceEqualityComparer.Instance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(HashSet<IInterceptorSubject> set)
    {
        set.Clear();
        _subjectSets ??= new Stack<HashSet<IInterceptorSubject>>();
        _subjectSets.Push(set);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stack<IInterceptorSubject> RentSubjectStack()
    {
        _subjectStacks ??= new Stack<Stack<IInterceptorSubject>>();
        return _subjectStacks.Count > 0 ? _subjectStacks.Pop() : new Stack<IInterceptorSubject>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(Stack<IInterceptorSubject> stack)
    {
        stack.Clear();
        _subjectStacks ??= new Stack<Stack<IInterceptorSubject>>();
        _subjectStacks.Push(stack);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<IInterceptorSubject, int> RentSubjectCounter()
    {
        _subjectCounters ??= new Stack<Dictionary<IInterceptorSubject, int>>();
        return _subjectCounters.Count > 0 ? _subjectCounters.Pop() : new Dictionary<IInterceptorSubject, int>(8, ReferenceEqualityComparer.Instance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(Dictionary<IInterceptorSubject, int> counter)
    {
        counter.Clear();
        _subjectCounters ??= new Stack<Dictionary<IInterceptorSubject, int>>();
        _subjectCounters.Push(counter);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<IInterceptorSubject, List<object?>> RentIndexGroups()
    {
        _indexGroups ??= new Stack<Dictionary<IInterceptorSubject, List<object?>>>();
        return _indexGroups.Count > 0 ? _indexGroups.Pop() : new Dictionary<IInterceptorSubject, List<object?>>(8, ReferenceEqualityComparer.Instance);
    }

    /// <summary>Returns the group map and every index list it handed out.</summary>
    public static void Return(Dictionary<IInterceptorSubject, List<object?>> groups)
    {
        foreach (var group in groups)
        {
            Return(group.Value);
        }

        groups.Clear();
        _indexGroups ??= new Stack<Dictionary<IInterceptorSubject, List<object?>>>();
        _indexGroups.Push(groups);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<(PropertyReference Property, SubjectOccurrence Occurrence)> RentChildList()
    {
        _childLists ??= new Stack<List<(PropertyReference, SubjectOccurrence)>>();
        return _childLists.Count > 0 ? _childLists.Pop() : new List<(PropertyReference, SubjectOccurrence)>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(List<(PropertyReference Property, SubjectOccurrence Occurrence)> list)
    {
        list.Clear();
        _childLists ??= new Stack<List<(PropertyReference, SubjectOccurrence)>>();
        _childLists.Push(list);
    }
}
