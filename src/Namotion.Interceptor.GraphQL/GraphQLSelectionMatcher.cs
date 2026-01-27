using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.GraphQL;

/// <summary>
/// Matches property changes against GraphQL selection paths.
/// </summary>
public static class GraphQLSelectionMatcher
{
    /// <summary>
    /// Checks if a property change matches any of the selected paths.
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

        // Build the path for this property change
        var pathParts = new List<string>();
        var current = registeredProperty;

        while (current is not null)
        {
            var segment = pathProvider.TryGetPropertySegment(current);
            if (segment is not null)
            {
                pathParts.Insert(0, segment);
            }

            if (ReferenceEquals(current.Parent.Subject, rootSubject))
            {
                break;
            }

            // Navigate to parent
            var parents = current.Parent.Parents;
            if (parents.Length > 0)
            {
                current = parents[0].Property;
            }
            else
            {
                break;
            }
        }

        if (pathParts.Count == 0)
        {
            return false;
        }

        var changePath = string.Join(".", pathParts);

        // Check for exact match or prefix match
        foreach (var selectedPath in selectedPaths)
        {
            // Exact match
            if (string.Equals(changePath, selectedPath, StringComparison.Ordinal))
            {
                return true;
            }

            // Changed property is parent of selected (e.g., "location" changed, "location.building" selected)
            if (selectedPath.StartsWith(changePath + ".", StringComparison.Ordinal))
            {
                return true;
            }

            // Changed property is child of selected (e.g., "location.building" changed, "location" selected)
            if (changePath.StartsWith(selectedPath + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts field paths from a GraphQL selection set string (simplified parsing).
    /// For now, accepts a list of field names.
    /// </summary>
    public static IReadOnlySet<string> ExtractSelectionPaths(IReadOnlyList<string> fieldNames)
    {
        return new HashSet<string>(fieldNames, StringComparer.Ordinal);
    }
}
