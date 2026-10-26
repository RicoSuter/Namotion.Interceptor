using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The metadata that the tools let their clients find and change: SearchMetadata of a storage finds only the subjects
/// and properties that the tools show and that SetMetadata lets them change, which no agent holds.
/// </summary>
internal static class AgentMetadataAccess
{
    // The tools return at most this many matches of SearchMetadata, as its description says; the method itself returns
    // every match.
    private const int MaxSearchMetadataMatches = 100;

    /// <summary>The refusal of SetMetadata for an agent or a subject that an agent holds.</summary>
    internal const string AgentMetadataMessage = "The metadata of agents cannot be changed.";

    /// <summary>
    /// Gets the first 100 matches of SearchMetadata of a storage without those that <see cref="GetError"/> refuses, and no
    /// match for a result of SearchMetadata of a storage that is no array of matches; any other result is returned unchanged.
    /// </summary>
    public static object? FilterSearchMetadataResult(
        RegisteredSubject subject, MethodMetadata method, object? result, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        if (method.PropertyName != nameof(FluentStorageContainer.SearchMetadata) || subject.Subject is not IMetadataStore)
        {
            return result;
        }

        if (result is not MetadataSearchMatch[] matches)
        {
            return Array.Empty<MetadataSearchMatch>();
        }

        return matches
            .Where(match => GetError(subject, match.Path, match.Property, pathProvider, excludeTypes) is null)
            .Take(MaxSearchMetadataMatches)
            .ToArray();
    }

    /// <summary>
    /// Gets why the metadata at <paramref name="path"/> of the storage is not shown by the tools or may not be set, or null
    /// when it is shown and may be set. The target is resolved as the tools resolve a path, so that a subject or property
    /// hidden from them is refused as a missing one; an agent and a subject that an agent holds through any parent are
    /// refused as well.
    /// </summary>
    public static string? GetError(
        RegisteredSubject storage, string path, string? propertyName, PathProviderBase pathProvider, Type[] excludeTypes)
    {
        if (TryGetVisibleTarget(storage, path, pathProvider, excludeTypes) is not { } target)
        {
            return FluentStorageContainer.GetNoSubjectMessage(path);
        }

        // Stands in for the system rule on agents until SetMetadata asks the access policy.
        if (target.Subject.TryGetFirstParentOrSelf<LlmAgentBase>() is not null)
        {
            return AgentMetadataMessage;
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
        var target = FluentStorageContainer.ResolveMetadataPath(storage.Subject, path);
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
        foreach (var current in new FirstParentChain(subject))
        {
            if (ReferenceEquals(current.Subject, top))
            {
                return false;
            }

            var type = current.Subject.GetType();
            foreach (var excludedType in excludeTypes)
            {
                if (excludedType.IsAssignableFrom(type))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The method checks the property only of a subject that a store keeps the metadata of, and not of a folder or a storage.
    private static bool IsPropertyChecked(IInterceptorSubject target)
        => target is not (FluentStorageContainer or VirtualFolder) && target.FindMetadataStore()?.CanStoreMetadata(target) == true;

    private static bool IsPropertyVisible(RegisteredSubject subject, string propertyName, PathProviderBase pathProvider)
        => subject.TryGetProperty(propertyName) is { IsAttribute: false } property && pathProvider.IsPropertyIncluded(property);
}
