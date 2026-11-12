using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The metadata that the tools let their clients change: SetMetadata changes only metadata of a subject or property that
/// the tools list and that the access policy allows to write.
/// </summary>
internal static class AgentMetadataAccess
{
    /// <summary>
    /// Gets why a call of an operation of a storage or folder with the given input arguments is refused, or null when it
    /// is not: SetMetadata with arguments of another shape or for metadata that the tools do not let the caller change,
    /// see <see cref="GetSetMetadataError"/> and <see cref="AgentOrderAccess.GetMetadataOrderError"/>, and SetOrder, see
    /// <see cref="AgentOrderAccess.GetSetOrderError"/>. Other operations are not refused.
    /// </summary>
    public static string? GetOperationError(
        RegisteredSubject subject, MethodMetadata method, object?[] arguments, McpServerConfiguration configuration, IInterceptorSubject root)
    {
        if (method.PropertyName == AgentOrderAccess.SetOrderMethodName && subject.Subject is IMetadataContainer)
        {
            return AgentOrderAccess.GetSetOrderError(subject, arguments, configuration, root);
        }

        if (method.PropertyName != FluentStorageContainer.SetMetadataMethodName || subject.Subject is not IMetadataStore)
        {
            return null;
        }

        // The input parameters in declaration order, empty when they could not be read. Any other shape is refused
        // without looking at the path, so that a subject the tools do not list reads as a missing one.
        if (arguments is not [string path, string or null, JsonElement { ValueKind: JsonValueKind.Object } values, ..])
        {
            return HomeBlazeMcpConfigurationFactory.SetMetadataArgumentsMessage;
        }

        var propertyName = arguments[1] as string;
        return GetSetMetadataError(subject, path, propertyName, configuration) ??
            (propertyName is null && FluentStorageContainer.ResolveMetadataPath(subject.Subject, path) is { } target
                ? AgentOrderAccess.GetMetadataOrderError(target, values, configuration, root)
                : null);
    }

    /// <summary>
    /// Gets why SetMetadata of the storage may not set the metadata at <paramref name="path"/>, or null when it may. The
    /// target is resolved as the tools resolve a path, so that a subject or property the tools do not list, because of an
    /// excluded type, the path provider or the access policy, is refused as a missing one; then the access policy must
    /// allow to write its metadata member.
    /// </summary>
    public static string? GetSetMetadataError(
        RegisteredSubject storage, string path, string? propertyName, McpServerConfiguration configuration)
    {
        if (TryGetListedTarget(storage, path, configuration) is not { } target)
        {
            return FluentStorageContainer.GetNoSubjectMessage(path);
        }

        if (propertyName is not null && IsPropertyChecked(target.Subject) && !IsPropertyReadable(target, propertyName, configuration))
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
        // list it and the next write sets it.
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

    /// <summary>
    /// Gets whether the metadata path of <paramref name="path"/> and <paramref name="propertyName"/>, see
    /// <see cref="HomeBlazeMarkdownSearchToolProvider.GetMetadataPath"/>, resolves from <paramref name="root"/>, as
    /// get_property does, to a readable member of <paramref name="subject"/> itself. It is not readable when the path
    /// resolves to a member of another subject or to a dictionary entry, as it can when the policy hides the member.
    /// </summary>
    internal static bool IsMetadataReadable(
        McpServerConfiguration configuration, RegisteredSubject root, RegisteredSubject subject, string path, string? propertyName)
        => configuration.TryGetReadableProperty(root, HomeBlazeMarkdownSearchToolProvider.GetMetadataPath(path, propertyName)) is
            { Index: null } resolved &&
            ReferenceEquals(resolved.Property.Parent.Subject, subject.Subject);

    // The subject at the path of the storage when the tools resolve the path to it and list it, else null.
    private static RegisteredSubject? TryGetListedTarget(RegisteredSubject storage, string path, McpServerConfiguration configuration)
    {
        var target = FluentStorageContainer.ResolveMetadataPath(storage.Subject, path);
        var trimmedPath = path.Trim();
        var listedTarget = trimmedPath.AsSpan().TrimStart('/').IsEmpty ? storage : configuration.TryGetReadableSubject(storage, trimmedPath);
        return target is not null && ReferenceEquals(listedTarget?.Subject, target) && !McpPageReadAccess.IsOfExcludedType(listedTarget, storage.Subject, configuration.ExcludeTypes)
            ? listedTarget
            : null;
    }

    // The method checks the property only of a subject that a store keeps the metadata of, and not of a folder or a storage.
    private static bool IsPropertyChecked(IInterceptorSubject target)
        => target is not (FluentStorageContainer or VirtualFolder) && target.FindMetadataStore()?.CanStoreMetadata(target) == true;

    private static bool IsPropertyReadable(
        RegisteredSubject subject, string propertyName, McpServerConfiguration configuration)
        => subject.TryGetProperty(propertyName) is { IsAttribute: false } property &&
            configuration.PathProvider.IsPropertyIncluded(property) &&
            configuration.IsAllowed(property, McpAccessActions.Read);
}
