using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

internal static class SubjectValueConvert
{
    internal static IReadOnlyList<IInterceptorSubject> ToSubjectList(object value)
    {
        if (value is IReadOnlyList<IInterceptorSubject> readOnlyList)
            return readOnlyList;

        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        var visitor = new SubjectListVisitor();
        SubjectValueVisitor.VisitCollectionSubjects(value, ref visitor);
        return visitor.List ?? (IReadOnlyList<IInterceptorSubject>)[];
    }

    internal static List<IInterceptorSubject> ToSubjectMutableList(object? value)
    {
        if (value is null)
            return [];

        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        var visitor = new SubjectListVisitor();
        SubjectValueVisitor.VisitCollectionSubjects(value, ref visitor);
        return visitor.List ?? [];
    }

    internal static IReadOnlyList<(object key, IInterceptorSubject subject)> ToSubjectDictionaryEntries(object value)
    {
        var visitor = new DictionaryEntryVisitor();
        SubjectValueVisitor.VisitDictionarySubjects(value, ref visitor);
        return visitor.Entries ?? (IReadOnlyList<(object, IInterceptorSubject)>)[];
    }

    private struct SubjectListVisitor : ISubjectValueVisitor
    {
        public List<IInterceptorSubject>? List;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSubject(IInterceptorSubject subject, object? indexOrKey)
        {
            List ??= [];
            List.Add(subject);
        }
    }

    private struct DictionaryEntryVisitor : ISubjectValueVisitor
    {
        public List<(object key, IInterceptorSubject subject)>? Entries;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSubject(IInterceptorSubject subject, object? indexOrKey)
        {
            Entries ??= [];
            Entries.Add((indexOrKey!, subject));
        }
    }
}
