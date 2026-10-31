using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
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
/// The order of the children of a folder or of the root of a storage that the tools let their clients read and change:
/// GetOrder names only the children that the tools show, SetOrder needs write access to the metadata of the folder, and
/// an order that names a child the tools do not show is refused as one naming a child that does not exist.
/// </summary>
internal static class AgentOrderAccess
{
    /// <summary>The name of the method that sets the order of a folder or of the root of a storage.</summary>
    internal const string SetOrderMethodName = "SetOrder";

    /// <summary>
    /// Gets the names of the result of GetOrder of a folder or of the root of a storage that are children the tools
    /// show, in their order; any other result is returned unchanged.
    /// </summary>
    public static object? FilterGetOrderResult(
        RegisteredSubject subject, MethodMetadata method, object? result, McpServerConfiguration configuration,
        PathProviderBase pathProvider, IInterceptorSubject root)
    {
        if (method.PropertyName != nameof(FluentStorageContainer.GetOrder) || subject.Subject is not IMetadataContainer || result is not string[] names)
        {
            return result;
        }

        var shownNames = GetShownChildNames(subject, configuration, pathProvider, root);
        return names.Where(shownNames.Contains).ToArray();
    }

    /// <summary>
    /// Gets why SetOrder of a folder or of the root of a storage is refused, or null when it is not: the access policy
    /// must allow to write the metadata of the folder, and every name must be a child that the tools show.
    /// </summary>
    /// <param name="folder">The folder or storage.</param>
    /// <param name="arguments">The input arguments of SetOrder, empty when they could not be read.</param>
    /// <param name="configuration">The configuration of the tools.</param>
    /// <param name="pathProvider">The path provider of the tools.</param>
    /// <param name="root">The root of the tools.</param>
    public static string? GetSetOrderError(
        RegisteredSubject folder, object?[] arguments, McpServerConfiguration configuration, PathProviderBase pathProvider, IInterceptorSubject root)
    {
        // A member that is added here and then refused keeps null, which is the same as no metadata.
        if (FluentStorageContainer.GetOrAddMetadataMember(folder, null) is not { } member || !configuration.IsAllowed(member, McpAccessActions.Write))
        {
            return AgentMetadataAccess.GetNotWritableMessage(GetToolPath(folder, pathProvider, root), null);
        }

        // Names of another shape are refused by the method.
        return arguments is [string[] names, ..] && Array.TrueForAll(names, static name => name is not null)
            ? FluentStorageContainer.GetOrderNamesError(names, GetShownChildNames(folder, configuration, pathProvider, root))
            : null;
    }

    /// <summary>
    /// Gets why the order in metadata values that SetMetadata writes to <paramref name="target"/> is refused because it
    /// names a child that the tools do not show, or null when it is not, also when the target is no folder or storage
    /// or the values have no order of names, which the method checks itself.
    /// </summary>
    public static string? GetMetadataOrderError(
        IInterceptorSubject target, JsonElement values, McpServerConfiguration configuration, PathProviderBase pathProvider, IInterceptorSubject root)
    {
        if (target is not IMetadataContainer ||
            target.TryGetRegisteredSubject() is not { } folder ||
            !values.TryGetProperty(MetadataKeys.Order, out var order) ||
            order.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>(order.GetArrayLength());
        foreach (var item in order.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            names.Add(item.GetString()!);
        }

        return FluentStorageContainer.GetOrderNamesError(names, GetShownChildNames(folder, configuration, pathProvider, root));
    }

    // The names of the children that the tools resolve at their path and show, as browse shows them.
    private static HashSet<string> GetShownChildNames(
        RegisteredSubject folder, McpServerConfiguration configuration, PathProviderBase pathProvider, IInterceptorSubject root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (folder.TryGetProperty(IMetadataContainer.ChildrenPropertyName) is not { } childrenProperty ||
            root.TryGetRegisteredSubject() is not { } registeredRoot)
        {
            return names;
        }

        foreach (var child in childrenProperty.Children)
        {
            if (child.Index is { } index && IsShown(child.Subject, childrenProperty.TryGetPath(pathProvider, root, index), folder, registeredRoot, configuration))
            {
                names.Add(index.ToString() ?? string.Empty);
            }
        }

        return names;
    }

    private static bool IsShown(
        IInterceptorSubject child, string? path, RegisteredSubject folder, RegisteredSubject root, McpServerConfiguration configuration)
        => path is not null &&
            configuration.TryGetReadableSubject(root, path) is { } shown &&
            ReferenceEquals(shown.Subject, child) &&
            !AgentMetadataAccess.IsOfExcludedType(shown, folder.Subject, configuration.ExcludeTypes);

    // The path at which the tools show the folder.
    private static string GetToolPath(RegisteredSubject folder, PathProviderBase pathProvider, IInterceptorSubject root)
    {
        var parents = folder.Parents;
        var path = ReferenceEquals(folder.Subject, root) || parents.Length == 0
            ? null
            : parents[0].Property.TryGetPath(pathProvider, root, parents[0].Index);
        return "/" + path;
    }
}
