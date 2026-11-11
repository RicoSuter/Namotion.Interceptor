using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Finds the later keys or positions of each subject a property value holds more than once, for its attach.
/// </summary>
internal static class AdditionalIndexCollector
{
    // Runs no user code while in use: subjects are compared by reference and keys are not compared at all.
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableFirstPlaces;

    private const int NoNextPlace = -1;

    /// <summary>
    /// Rents an array with an entry per child: at the first place of each subject held more than once and not kept its
    /// later keys or positions, in order; default at every other place. Give it back with <see cref="Return"/>.
    /// </summary>
    /// <param name="children">The subjects one property value holds, as the lifecycle interceptor scanned them.</param>
    /// <param name="kept">The subjects the property held before, which are not attached, or null.</param>
    /// <returns>The rented array, which may be longer than <paramref name="children"/>.</returns>
    public static ImmutableArray<object?>[] Rent(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> children,
        HashSet<IInterceptorSubject>? kept)
    {
        var additionalIndices = ArrayPool<ImmutableArray<object?>>.Shared.Rent(children.Length);
        Collect(children, kept, additionalIndices.AsSpan(0, children.Length));
        return additionalIndices;
    }

    /// <summary>Gives back an array from <see cref="Rent"/>.</summary>
    /// <param name="additionalIndices">The rented array.</param>
    public static void Return(ImmutableArray<object?>[] additionalIndices)
    {
        // Release references so subjects can be GC'd.
        ArrayPool<ImmutableArray<object?>>.Shared.Return(additionalIndices, clearArray: true);
    }

    private static void Collect(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> children,
        HashSet<IInterceptorSubject>? kept, Span<ImmutableArray<object?>> additionalIndices)
    {
        additionalIndices.Clear();

        var firstPlaces = _reusableFirstPlaces ??= new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        var nextPlaces = ArrayPool<int>.Shared.Rent(children.Length);
        try
        {
            // Backwards, so that each place links to the next place of its subject and the map ends at the first.
            for (var i = children.Length - 1; i >= 0; i--)
            {
                ref var first = ref CollectionsMarshal.GetValueRefOrAddDefault(firstPlaces, children[i].subject, out var exists);
                nextPlaces[i] = exists ? first : NoNextPlace;
                first = i;
            }

            foreach (var first in firstPlaces.Values)
            {
                if (nextPlaces[first] != NoNextPlace && (kept is null || !kept.Contains(children[first].subject)))
                {
                    additionalIndices[first] = GetLaterIndices(children, nextPlaces, first);
                }
            }
        }
        finally
        {
            // Release references so subjects can be GC'd on idle threads.
            firstPlaces.Clear();
            ArrayPool<int>.Shared.Return(nextPlaces);
        }
    }

    private static ImmutableArray<object?> GetLaterIndices(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> children,
        int[] nextPlaces, int first)
    {
        var count = 0;
        for (var place = nextPlaces[first]; place != NoNextPlace; place = nextPlaces[place])
        {
            count++;
        }

        var indices = new object?[count];
        count = 0;
        for (var place = nextPlaces[first]; place != NoNextPlace; place = nextPlaces[place])
        {
            indices[count++] = children[place].index;
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(indices);
    }
}
