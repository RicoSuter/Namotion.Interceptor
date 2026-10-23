using System.Collections.Immutable;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Element-wise equality of immutable arrays for the value equality of records, a <c>default</c> array being equal to an empty one.</summary>
internal static class SequenceEquality
{
    public static bool AreEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right, IEqualityComparer<T>? comparer = null)
    {
        var leftSpan = left.IsDefault ? [] : left.AsSpan();
        var rightSpan = right.IsDefault ? [] : right.AsSpan();
        return leftSpan.SequenceEqual(rightSpan, comparer);
    }

    public static void AddTo<T>(ref HashCode hash, ImmutableArray<T> array, IEqualityComparer<T>? comparer = null)
    {
        if (array.IsDefault)
        {
            hash.Add(0);
            return;
        }

        hash.Add(array.Length);
        foreach (var item in array)
        {
            hash.Add(item, comparer);
        }
    }
}
