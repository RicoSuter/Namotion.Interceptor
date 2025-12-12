using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Sources.Transactions;

/// <summary>
/// Extension methods for associating properties with their external data sources.
/// </summary>
/// <remarks>
/// These extensions use the property data storage mechanism to maintain a mapping between
/// properties and their corresponding <see cref="ISubjectSource"/> instances. This association
/// is used by the <see cref="SubjectSourceBackgroundService"/> to dispatch changes only to the
/// owning source, and by transactions to determine which source to write changes to during commit.
/// Each property can have at most one source (single owner model).
/// </remarks>
public static class SourcePropertyExtensions
{
    private const string SourceKey = "Namotion.Interceptor.Sources.Source";

    /// <summary>
    /// Associates a property with its external data source.
    /// Each property can only have one source at a time. If the property already has a different
    /// source, a warning is logged and the source is replaced.
    /// </summary>
    /// <param name="property">The property reference to associate with a source.</param>
    /// <param name="source">The external data source that provides and synchronizes this property's value.</param>
    /// <param name="logger">Optional logger to log warnings when replacing an existing source.</param>
    public static void SetSource(this PropertyReference property, ISubjectSource source, ILogger? logger = null)
    {
        if (logger is not null &&
            property.TryGetSource(out var existing) &&
            existing != source)
        {
            logger.LogWarning(
                "Property {Subject}.{Property} already bound to source {ExistingSource}, " +
                "replacing with {NewSource}. This may indicate a configuration error.",
                property.Subject.GetType().Name,
                property.Name,
                existing.GetType().Name,
                source.GetType().Name);
        }

        property.SetPropertyData(SourceKey, source);
    }

    /// <summary>
    /// Gets the external data source associated with a property, if any.
    /// </summary>
    /// <param name="property">The property reference to query.</param>
    /// <param name="source">
    /// When this method returns, contains the associated source if found; otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the property has an associated source; otherwise, <c>false</c>.
    /// </returns>
    public static bool TryGetSource(this PropertyReference property, [NotNullWhen(true)] out ISubjectSource? source)
    {
        if (property.TryGetPropertyData(SourceKey, out var data) && data is ISubjectSource s)
        {
            source = s;
            return true;
        }
        source = null;
        return false;
    }

    /// <summary>
    /// Removes the source association from a property.
    /// </summary>
    /// <param name="property">The property reference to disassociate from its source.</param>
    public static void RemoveSource(this PropertyReference property)
    {
        property.RemovePropertyData(SourceKey);
    }
}
