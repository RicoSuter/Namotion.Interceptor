using System.Buffers;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.GraphQL;

/// <summary>
/// Matches property changes against GraphQL selection paths.
/// </summary>
public static class GraphQLSelectionMatcher
{
    private const int MaxPathDepth = 16;
    private const char PathSeparator = '.';

    /// <summary>
    /// Checks if a property change matches any of the selected paths.
    /// The paths use '.' as separator, matching the GraphQL field nesting convention.
    /// </summary>
    public static bool IsPropertyInSelection(
        SubjectPropertyChange change,
        IReadOnlySet<string> selectedPaths,
        IPathProvider pathProvider,
        IInterceptorSubject rootSubject)
    {
        var registeredProperty = change.Property.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return false;
        }

        // Collect path segments leaf-to-root using a pooled array to avoid List allocation.
        var segments = ArrayPool<string>.Shared.Rent(MaxPathDepth);
        try
        {
            var segmentCount = 0;
            var totalLength = 0;
            var current = registeredProperty;

            while (current is not null)
            {
                var segment = pathProvider.TryGetPropertySegment(current);
                if (segment is not null)
                {
                    if (segmentCount >= MaxPathDepth)
                    {
                        // Deeper than expected; fall through to receive all changes.
                        return true;
                    }

                    segments[segmentCount++] = segment;
                    totalLength += segment.Length;
                }

                if (ReferenceEquals(current.Parent.Subject, rootSubject))
                {
                    break;
                }

                var parents = current.Parent.Parents;
                current = parents.Length > 0 ? parents[0].Property : null;
            }

            if (segmentCount == 0)
            {
                return false;
            }

            // Reverse to get root-to-leaf order.
            Array.Reverse(segments, 0, segmentCount);

            // Build the full path into a stackalloc buffer to avoid string allocation.
            var pathLength = totalLength + (segmentCount - 1);
            Span<char> changePath = stackalloc char[pathLength];
            var position = 0;

            for (var i = 0; i < segmentCount; i++)
            {
                if (i > 0)
                {
                    changePath[position++] = PathSeparator;
                }

                segments[i].AsSpan().CopyTo(changePath[position..]);
                position += segments[i].Length;
            }

            // Check for exact match or prefix match.
            foreach (var selectedPath in selectedPaths)
            {
                var selected = selectedPath.AsSpan();

                // Exact match
                if (changePath.SequenceEqual(selected))
                {
                    return true;
                }

                // Changed property is parent of selected
                // (e.g., "location" changed, "location.building" selected)
                if (selected.Length > changePath.Length
                    && selected[changePath.Length] == PathSeparator
                    && selected.StartsWith(changePath))
                {
                    return true;
                }

                // Changed property is child of selected
                // (e.g., "location.building" changed, "location" selected)
                if (changePath.Length > selected.Length
                    && changePath[selected.Length] == PathSeparator
                    && changePath.StartsWith(selected))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(segments, clearArray: true);
        }
    }
}
