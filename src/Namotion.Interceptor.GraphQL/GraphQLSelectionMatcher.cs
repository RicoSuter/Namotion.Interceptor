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
                pathParts.Add(segment);
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

        pathParts.Reverse();

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
            if (selectedPath.Length > changePath.Length
                && selectedPath[changePath.Length] == '.'
                && selectedPath.AsSpan().StartsWith(changePath.AsSpan()))
            {
                return true;
            }

            // Changed property is child of selected (e.g., "location.building" changed, "location" selected)
            if (changePath.Length > selectedPath.Length
                && changePath[selectedPath.Length] == '.'
                && changePath.AsSpan().StartsWith(selectedPath.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }
}
