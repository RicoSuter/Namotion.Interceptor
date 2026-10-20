using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The metadata that the tools let agents change and find: SetMetadata and SearchMetadata of a storage reach the same
/// subjects and properties, those that the tools show and that no agent holds.
/// </summary>
internal static class AgentMetadataAccess
{
    // Guards against a cycle of parents, which a walk up would follow forever.
    private const int MaxDepth = 256;

    // The description of SearchMetadata names this limit.
    private const int MaxSearchMetadataMatches = 100;

    /// <summary>
    /// Gets the first 100 matches of SearchMetadata of a storage, for agents without those that
    /// <see cref="GetSetMetadataError"/> refuses; any other result is returned unchanged.
    /// </summary>
    public static object? FilterSearchMetadataResult(
        RegisteredSubject subject, MethodMetadata method, object? result, bool forAgents, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        if (method.PropertyName != FluentStorageContainer.SearchMetadataMethodName ||
            subject.Subject is not FluentStorageContainer ||
            result is not MetadataSearchMatch[] matches)
        {
            return result;
        }

        return matches
            .Where(match => !forAgents || GetSetMetadataError(subject, match.Path, match.Property, pathProvider, excludeTypes) is null)
            .Take(MaxSearchMetadataMatches)
            .ToArray();
    }

    /// <summary>
    /// Gets why an agent may not set the metadata at <paramref name="path"/> of the storage, or null when it may. The target
    /// is resolved as the tools resolve a path, so that a subject or property hidden from agents is refused as a missing
    /// one, and an agent and a subject that an agent holds through any parent are refused.
    /// </summary>
    public static string? GetSetMetadataError(
        RegisteredSubject storage, string path, string? propertyName, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        var target = MetadataPaths.TryResolveSubject(storage.Subject, path);
        var trimmedPath = path.Trim();
        var visibleTarget = trimmedPath.AsSpan().TrimStart('/').IsEmpty ? storage : pathProvider.TryGetSubjectFromPath(storage, trimmedPath);
        if (target is null || !ReferenceEquals(visibleTarget?.Subject, target) || IsOfExcludedType(visibleTarget, storage.Subject, excludeTypes))
        {
            return FluentStorageContainer.GetNoSubjectMessage(path);
        }

        if (target.TryGetFirstParentOrSelf<LlmAgentBase>() is not null)
        {
            return "Agents cannot change the metadata of agents.";
        }

        if (propertyName is not null && IsPropertyChecked(target) && !IsPropertyVisible(visibleTarget, propertyName, pathProvider))
        {
            return FluentStorageContainer.GetNoPropertyMessage(propertyName);
        }

        return null;
    }

    /// <summary>
    /// Gets whether the subject or one of the subjects that hold it below <paramref name="top"/>, through the first parent
    /// of each, is of one of <paramref name="excludeTypes"/>.
    /// </summary>
    public static bool IsOfExcludedType(RegisteredSubject subject, IInterceptorSubject top, Type[] excludeTypes)
    {
        var current = subject;
        for (var depth = 0; depth < MaxDepth && !ReferenceEquals(current.Subject, top); depth++)
        {
            var type = current.Subject.GetType();
            if (Array.Exists(excludeTypes, excludedType => excludedType.IsAssignableFrom(type)))
            {
                return true;
            }

            if (current.Parents is not [var parent, ..])
            {
                break;
            }

            current = parent.Property.Parent;
        }

        return false;
    }

    // The method checks the property only of a subject that a store keeps the metadata of, and not of a folder or a storage.
    private static bool IsPropertyChecked(IInterceptorSubject target)
        => target is not (FluentStorageContainer or VirtualFolder) && target.FindMetadataStore()?.CanStoreMetadata(target) == true;

    private static bool IsPropertyVisible(RegisteredSubject subject, string propertyName, PathProviderBase pathProvider)
        => subject.TryGetProperty(propertyName) is { IsAttribute: false } property && pathProvider.IsPropertyIncluded(property);
}
