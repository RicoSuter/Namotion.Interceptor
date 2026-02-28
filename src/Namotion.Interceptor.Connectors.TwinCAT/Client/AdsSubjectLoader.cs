using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Recursively loads the subject graph and maps properties to ADS symbol paths.
/// Pure and testable - no ADS client dependency.
/// </summary>
internal sealed class AdsSubjectLoader
{
    private readonly IPathProvider _pathProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsSubjectLoader"/> class.
    /// </summary>
    /// <param name="pathProvider">The path provider used to resolve property segments.</param>
    public AdsSubjectLoader(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    /// <summary>
    /// Loads all leaf properties from the subject graph with their ADS symbol paths.
    /// </summary>
    /// <param name="rootSubject">The root subject to start loading from.</param>
    /// <returns>A list of tuples containing the registered property and its resolved ADS symbol path.</returns>
    public IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath)> LoadSubjectGraph(
        IInterceptorSubject rootSubject)
    {
        var result = new List<(RegisteredSubjectProperty, string)>();
        var loadedSubjects = new HashSet<IInterceptorSubject>();
        LoadSubjectRecursive(rootSubject, null, loadedSubjects, result);
        return result;
    }

    private void LoadSubjectRecursive(
        IInterceptorSubject subject,
        string? parentPath,
        HashSet<IInterceptorSubject> loadedSubjects,
        List<(RegisteredSubjectProperty, string)> result)
    {
        if (!loadedSubjects.Add(subject))
        {
            return;
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        foreach (var property in registeredSubject.Properties)
        {
            if (!_pathProvider.IsPropertyIncluded(property))
            {
                continue;
            }

            var segment = _pathProvider.TryGetPropertySegment(property);
            if (segment is null)
            {
                continue;
            }

            var propertyPath = parentPath is null ? segment : $"{parentPath}.{segment}";

            // Check dictionary and collection before subject reference because
            // interface types like IList<T> and IDictionary<K,V> also satisfy IsSubjectReference.
            if (property.IsSubjectDictionary)
            {
                foreach (var child in property.Children)
                {
                    var childPath = $"{propertyPath}.{child.Index}";
                    LoadSubjectRecursive(child.Subject, childPath, loadedSubjects, result);
                }
            }
            else if (property.IsSubjectCollection)
            {
                var index = 0;
                foreach (var child in property.Children)
                {
                    var childPath = $"{propertyPath}[{index}]";
                    LoadSubjectRecursive(child.Subject, childPath, loadedSubjects, result);
                    index++;
                }
            }
            else if (property.IsSubjectReference)
            {
                var child = property.Children.SingleOrDefault();
                if (child.Subject is not null)
                {
                    LoadSubjectRecursive(child.Subject, propertyPath, loadedSubjects, result);
                }
            }
            else
            {
                // Scalar or primitive array - single ADS symbol
                result.Add((property, propertyPath));
            }
        }
    }
}
