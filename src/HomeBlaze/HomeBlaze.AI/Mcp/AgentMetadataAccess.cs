using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The metadata that the tools let their clients find and change: SearchMetadata of a storage finds only the subjects
/// and properties that the tools show and whose metadata the access policy allows to read, and SetMetadata changes only
/// metadata of a subject or property that the tools show and that the access policy allows to write.
/// </summary>
internal static class AgentMetadataAccess
{
    // The tools return at most this many matches of SearchMetadata, as its description says; the method itself returns
    // every match.
    private const int MaxSearchMetadataMatches = 100;

    /// <summary>
    /// Gets the first 100 matches of SearchMetadata of a storage that the tools show and whose metadata member the access
    /// policy of <paramref name="configuration"/> allows to read at its path, as get_property reads it, and no match for a
    /// result of SearchMetadata of a storage that is no array of matches; any other result is returned unchanged.
    /// </summary>
    public static object? FilterSearchMetadataResult(
        RegisteredSubject subject, MethodMetadata method, object? result, McpServerConfiguration configuration, PathProviderBase pathProvider)
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
            .Where(match => IsReadable(subject, match, configuration, pathProvider))
            .Take(MaxSearchMetadataMatches)
            .ToArray();
    }

    /// <summary>
    /// Gets why a call of an operation of a storage or folder with the given input arguments is refused, or null when it
    /// is not: SetMetadata with arguments of another shape or for metadata that the tools do not let the caller change,
    /// see <see cref="GetSetMetadataError"/> and <see cref="AgentOrderAccess.GetMetadataOrderError"/>, and SetOrder, see
    /// <see cref="AgentOrderAccess.GetSetOrderError"/>. Other operations are not refused.
    /// </summary>
    public static string? GetOperationError(
        RegisteredSubject subject, MethodMetadata method, object?[] arguments, McpServerConfiguration configuration,
        PathProviderBase pathProvider, IInterceptorSubject root)
    {
        if (method.PropertyName == AgentOrderAccess.SetOrderMethodName && subject.Subject is IMetadataContainer)
        {
            return AgentOrderAccess.GetSetOrderError(subject, arguments, configuration, pathProvider, root);
        }

        if (method.PropertyName != FluentStorageContainer.SetMetadataMethodName || subject.Subject is not IMetadataStore)
        {
            return null;
        }

        // The input parameters in declaration order, empty when they could not be read. Any other shape is refused
        // without looking at the path, so that a subject the tools do not show reads as a missing one.
        if (arguments is not [string path, string or null, JsonElement { ValueKind: JsonValueKind.Object } values, ..])
        {
            return HomeBlazeMcpConfigurationFactory.SetMetadataArgumentsMessage;
        }

        var propertyName = arguments[1] as string;
        return GetSetMetadataError(subject, path, propertyName, configuration, pathProvider) ??
            (propertyName is null && FluentStorageContainer.ResolveMetadataPath(subject.Subject, path) is { } target
                ? AgentOrderAccess.GetMetadataOrderError(target, values, configuration, pathProvider, root)
                : null);
    }

    /// <summary>
    /// Gets why SetMetadata of the storage may not set the metadata at <paramref name="path"/>, or null when it may. The
    /// target is resolved as the tools resolve a path, so that a subject or property hidden from them, by an excluded type
    /// or by the access policy, is refused as a missing one; then the access policy must allow to write its metadata member.
    /// </summary>
    public static string? GetSetMetadataError(
        RegisteredSubject storage, string path, string? propertyName, McpServerConfiguration configuration, PathProviderBase pathProvider)
    {
        if (TryGetVisibleTarget(storage, path, configuration) is not { } target)
        {
            return FluentStorageContainer.GetNoSubjectMessage(path);
        }

        if (propertyName is not null && IsPropertyChecked(target.Subject) && !IsPropertyVisible(target, propertyName, configuration, pathProvider))
        {
            return FluentStorageContainer.GetNoPropertyMessage(propertyName);
        }

        // A folder and the root of a storage have no metadata of properties, which the method refuses.
        return propertyName is not null && target.Subject is FluentStorageContainer or VirtualFolder
            ? null
            : GetWriteError(target, path, propertyName, configuration);
    }

    /// <summary>The refusal of SetMetadata for metadata that the access policy does not allow to write.</summary>
    internal static string GetNotWritableMessage(string path, string? propertyName) => propertyName is null
        ? $"Metadata of '{path}' is not writable. The access policy does not allow it."
        : $"Metadata of property '{propertyName}' of '{path}' is not writable. The access policy does not allow it.";

    private static string? GetWriteError(RegisteredSubject target, string path, string? propertyName, McpServerConfiguration configuration)
    {
        // A member that is added here and then refused keeps null, which is the same as no metadata: the tools do not
        // show it and the next write sets it.
        if (FluentStorageContainer.GetOrAddMetadataMember(target, propertyName) is { } member)
        {
            return configuration.IsAllowed(member, McpAccessActions.Write) ? null : GetNotWritableMessage(path, propertyName);
        }

        // Without a member, the property cannot have metadata, which the method refuses, or the subject attached again
        // since it was resolved, so that the policy did not decide on it.
        return propertyName is not null && ReferenceEquals(target.Subject.TryGetRegisteredSubject(), target)
            ? null
            : GetNotWritableMessage(path, propertyName);
    }

    // The match is shown, and its metadata member is readable at the path at which get_property reads it.
    private static bool IsReadable(RegisteredSubject storage, MetadataSearchMatch match, McpServerConfiguration configuration, PathProviderBase pathProvider)
    {
        if (TryGetVisibleTarget(storage, match.Path, configuration) is not { } target ||
            (match.Property is not null && IsPropertyChecked(target.Subject) && !IsPropertyVisible(target, match.Property, configuration, pathProvider)))
        {
            return false;
        }

        return configuration.AccessPolicy is null ||
            configuration.TryGetReadableProperty(storage, HomeBlazeMarkdownSearchToolProvider.GetMetadataPath(match.Path, match.Property)) is not null;
    }

    // The subject at the path of the storage when the tools resolve the path to it and show it, else null.
    private static RegisteredSubject? TryGetVisibleTarget(RegisteredSubject storage, string path, McpServerConfiguration configuration)
    {
        var target = FluentStorageContainer.ResolveMetadataPath(storage.Subject, path);
        var trimmedPath = path.Trim();
        var visibleTarget = trimmedPath.AsSpan().TrimStart('/').IsEmpty ? storage : configuration.TryGetReadableSubject(storage, trimmedPath);
        return target is not null && ReferenceEquals(visibleTarget?.Subject, target) && !IsOfExcludedType(visibleTarget, storage.Subject, configuration.ExcludeTypes)
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

    private static bool IsPropertyVisible(
        RegisteredSubject subject, string propertyName, McpServerConfiguration configuration, PathProviderBase pathProvider)
        => subject.TryGetProperty(propertyName) is { IsAttribute: false } property &&
            pathProvider.IsPropertyIncluded(property) &&
            configuration.IsAllowed(property, McpAccessActions.Read);
}
