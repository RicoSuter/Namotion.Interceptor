using System.Collections.Immutable;
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
/// The order of the children of a folder or of the root of a storage that the tools let their clients read and change:
/// GetOrder, the order in the metadata of the folder and its ChildOrder name only the children that the tools list,
/// SetOrder needs write access to the metadata of the folder, and an order that names a child the tools do not list is
/// refused as one naming a child that does not exist.
/// </summary>
internal static class AgentOrderAccess
{
    /// <summary>The name of the method that sets the order of a folder or of the root of a storage.</summary>
    internal const string SetOrderMethodName = "SetOrder";

    /// <summary>
    /// Gets the names of the result of GetOrder of a folder or of the root of a storage that are children the tools
    /// list, in their order; any other result is returned unchanged.
    /// </summary>
    public static object? FilterGetOrderResult(
        RegisteredSubject subject, MethodMetadata method, object? result, McpServerConfiguration configuration, IInterceptorSubject root)
    {
        if (method.PropertyName != nameof(FluentStorageContainer.GetOrder) || subject.Subject is not IMetadataContainer || result is not string[] names)
        {
            return result;
        }

        var listedNames = GetListedChildNames(subject, configuration, root);
        return names.Where(listedNames.Contains).ToArray();
    }

    /// <summary>
    /// Creates the value filter that leaves out of the order in the metadata of a folder or of the root of a storage, and
    /// out of its ChildOrder, the names of the children that the tools do not list, as GetOrder does, so that the tools
    /// and page expressions name no hidden child.
    /// </summary>
    public static IMcpValueFilter CreateOrderFilter(McpServerConfiguration configuration, Func<IInterceptorSubject> rootSubjectProvider)
        => new OrderFilter(configuration, rootSubjectProvider);

    /// <summary>
    /// Gets why SetOrder of a folder or of the root of a storage is refused, or null when it is not: the access policy
    /// must allow to write the metadata of the folder, and every name must be a child that the tools list.
    /// </summary>
    /// <param name="folder">The folder or storage.</param>
    /// <param name="arguments">The input arguments of SetOrder, empty when they could not be read.</param>
    /// <param name="configuration">The configuration of the tools.</param>
    /// <param name="root">The root of the tools.</param>
    public static string? GetSetOrderError(
        RegisteredSubject folder, object?[] arguments, McpServerConfiguration configuration, IInterceptorSubject root)
    {
        // A member that is added here and then refused keeps null, which is the same as no metadata.
        if (FluentStorageContainer.GetOrAddMetadataMember(folder, null) is not { } member || !configuration.IsAllowed(member, McpAccessActions.Write))
        {
            return AgentMetadataAccess.GetNotWritableMessage(GetToolPath(folder, configuration, root), null);
        }

        // Names of another shape are refused by the method.
        return arguments is [string[] names, ..] && Array.TrueForAll(names, static name => name is not null)
            ? FluentStorageContainer.GetOrderNamesError(names, GetListedChildNames(folder, configuration, root))
            : null;
    }

    /// <summary>
    /// Gets why the order in metadata values that SetMetadata writes to <paramref name="target"/> is refused because it
    /// names a child that the tools do not list, or null when it is not, also when the target is no folder or storage
    /// or the values have no order of names, which the method checks itself.
    /// </summary>
    public static string? GetMetadataOrderError(
        IInterceptorSubject target, JsonElement values, McpServerConfiguration configuration, IInterceptorSubject root)
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

        return FluentStorageContainer.GetOrderNamesError(names, GetListedChildNames(folder, configuration, root));
    }

    // The names of the children that the tools resolve at their path and list, as browse lists them.
    private static HashSet<string> GetListedChildNames(
        RegisteredSubject folder, McpServerConfiguration configuration, IInterceptorSubject root)
    {
        if (root.TryGetRegisteredSubject() is not { } registeredRoot)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return ChildOrdering.GetListedNames(
            folder,
            (Configuration: configuration, Root: root, RegisteredRoot: registeredRoot),
            static (state, childrenProperty, child) => IsListed(
                child.Subject,
                childrenProperty.TryGetPath(state.Configuration.PathProvider, state.Root, child.Index),
                childrenProperty.Parent,
                state.RegisteredRoot,
                state.Configuration));
    }

    private static bool IsListed(
        IInterceptorSubject child, string? path, RegisteredSubject folder, RegisteredSubject root, McpServerConfiguration configuration)
        => path is not null &&
            configuration.TryGetReadableSubject(root, path) is { } listed &&
            ReferenceEquals(listed.Subject, child) &&
            !AgentMetadataAccess.IsOfExcludedType(listed, folder.Subject, configuration.ExcludeTypes);

    // The path that the tools build for the folder.
    private static string GetToolPath(RegisteredSubject folder, McpServerConfiguration configuration, IInterceptorSubject root)
        => (root.TryGetRegisteredSubject() is { } registeredRoot ? configuration.TryGetSubjectPath(registeredRoot, folder) : null) ?? "/";

    private sealed class OrderFilter(McpServerConfiguration configuration, Func<IInterceptorSubject> rootSubjectProvider) : IMcpValueFilter
    {
        public object? Filter(RegisteredSubjectProperty member, object? value) =>
            ChildOrdering.FilterOrder(
                member,
                value,
                (Configuration: configuration, RootSubjectProvider: rootSubjectProvider),
                static (state, folder) => GetListedChildNames(folder, state.Configuration, state.RootSubjectProvider()));
    }
}
