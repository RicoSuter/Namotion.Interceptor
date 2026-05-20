using System.Collections;
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

        return CollectSubjects(value) ?? (IReadOnlyList<IInterceptorSubject>)[];
    }

    internal static List<IInterceptorSubject> ToSubjectMutableList(object? value)
    {
        if (value is null)
            return [];

        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        return CollectSubjects(value) ?? [];
    }

    internal static IReadOnlyList<(object key, IInterceptorSubject subject)> ToSubjectDictionaryEntries(object value)
    {
        List<(object key, IInterceptorSubject subject)>? entries = null;

        if (value is IInterceptorSubject single)
        {
            entries = [(null!, single)];
        }
        else if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is IInterceptorSubject subjectItem)
                {
                    entries ??= [];
                    entries.Add((entry.Key, subjectItem));
                }
            }
        }
        else if (value is not string && value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                if (SubjectValueVisitor.TryGetKvpSubjectEntry(item, out var key, out var subject))
                {
                    entries ??= [];
                    entries.Add((key!, subject));
                }
            }
        }

        return entries ?? (IReadOnlyList<(object, IInterceptorSubject)>)[];
    }

    private static List<IInterceptorSubject>? CollectSubjects(object value)
    {
        List<IInterceptorSubject>? list = null;

        if (value is IInterceptorSubject single)
        {
            list = [single];
        }
        else if (value is not string && value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is IInterceptorSubject subjectItem)
                {
                    list ??= [];
                    list.Add(subjectItem);
                }
            }
        }

        return list;
    }
}
