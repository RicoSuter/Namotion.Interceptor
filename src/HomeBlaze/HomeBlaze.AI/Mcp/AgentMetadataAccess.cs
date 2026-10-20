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
/// The metadata that the tools let their clients find and agents change: SearchMetadata of a storage finds only the
/// subjects and properties that the tools show, and for agents only those that SetMetadata lets them change, which no
/// agent holds.
/// </summary>
internal static class AgentMetadataAccess
{
    // Guards against a cycle of parents, which a walk up would follow forever.
    private const int MaxDepth = 256;

    // The tools return at most this many matches of SearchMetadata, as its description says; the method itself returns
    // every match.
    private const int MaxSearchMetadataMatches = 100;

    /// <summary>
    /// Gets the first 100 matches of SearchMetadata of a storage without those of a subject or property that the tools
    /// do not show, and for agents also without those that <see cref="GetSetMetadataError"/> refuses; any other result is
    /// returned unchanged.
    /// </summary>
    public static object? FilterSearchMetadataResult(
        RegisteredSubject subject, MethodMetadata method, object? result, bool forAgents, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        if (method.PropertyName != nameof(FluentStorageContainer.SearchMetadata) ||
            subject.Subject is not FluentStorageContainer ||
            result is not MetadataSearchMatch[] matches)
        {
            return result;
        }

        return matches
            .Where(match => GetError(subject, match.Path, match.Property, pathProvider, excludeTypes, forAgents) is null)
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
        => GetError(storage, path, propertyName, pathProvider, excludeTypes, forAgents: true);

    // The checks of GetSetMetadataError; without forAgents, only those of what the tools show.
    private static string? GetError(
        RegisteredSubject storage, string path, string? propertyName, PathProviderBase pathProvider, Type[] excludeTypes, bool forAgents)
    {
        if (TryGetVisibleTarget(storage, path, pathProvider, excludeTypes) is not { } target)
        {
            return FluentStorageContainer.GetNoSubjectMessage(path);
        }

        if (forAgents && target.Subject.TryGetFirstParentOrSelf<LlmAgentBase>() is not null)
        {
            return "Agents cannot change the metadata of agents.";
        }

        if (propertyName is not null && IsPropertyChecked(target.Subject) && !IsPropertyVisible(target, propertyName, pathProvider))
        {
            return FluentStorageContainer.GetNoPropertyMessage(propertyName);
        }

        return null;
    }

    // The subject at the path of the storage when the tools resolve the path to it and show it, else null.
    private static RegisteredSubject? TryGetVisibleTarget(RegisteredSubject storage, string path, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        var target = MetadataPaths.TryResolveSubject(storage.Subject, path);
        var trimmedPath = path.Trim();
        var visibleTarget = trimmedPath.AsSpan().TrimStart('/').IsEmpty ? storage : pathProvider.TryGetSubjectFromPath(storage, trimmedPath);
        return target is not null && ReferenceEquals(visibleTarget?.Subject, target) && !IsOfExcludedType(visibleTarget, storage.Subject, excludeTypes)
            ? visibleTarget
            : null;
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
