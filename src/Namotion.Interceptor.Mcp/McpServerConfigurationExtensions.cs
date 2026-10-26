using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Access checks of a tool configuration, for the tools of this package and of an application alike.
/// </summary>
public static class McpServerConfigurationExtensions
{
    /// <summary>
    /// Whether <see cref="McpServerConfiguration.AccessPolicy"/> allows the action on the member; true when there is
    /// no policy.
    /// </summary>
    public static bool IsAllowed(this McpServerConfiguration configuration, RegisteredSubjectProperty member, string action)
        => configuration.AccessPolicy?.IsAllowed(member, action) ?? true;

    /// <summary>
    /// Resolves a property path from <paramref name="root"/> as get_property does. With an
    /// <see cref="McpServerConfiguration.AccessPolicy"/>, every property on the path must be one the policy allows to
    /// read, an attribute together with its property, and the subject owning the resolved property must be shown, else
    /// the path reads as missing. A subject is shown when it is <paramref name="root"/>, or when it is not empty for the
    /// policy (it has a property other than an attribute and the policy denies read on every one of them) and every
    /// property on the path the tools write for it is readable; so a subject hidden in browse is not reachable through
    /// another parent either.
    /// </summary>
    /// <returns>The property and the key of its last segment, as <see cref="PathExtensions.TryGetPropertyFromPath(PathProviderBase, RegisteredSubject, string)"/> returns them, or null.</returns>
    public static (RegisteredSubjectProperty Property, object? Index)? TryGetReadableProperty(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => configuration.TryGetReadableProperty(root, path, McpVisibility.For(configuration, root.Subject));

    /// <summary>
    /// <see cref="TryGetReadableProperty(McpServerConfiguration, RegisteredSubject, string)"/> with the visibility of
    /// the current call, whose root is <paramref name="root"/>.
    /// </summary>
    internal static (RegisteredSubjectProperty Property, object? Index)? TryGetReadableProperty(
        this McpServerConfiguration configuration, RegisteredSubject root, string path, McpVisibility visibility)
    {
        if (configuration.AccessPolicy is null)
        {
            return configuration.ResolutionPathProvider.TryGetPropertyFromPath(root, path);
        }

        return configuration.ResolutionPathProvider.TryGetPropertyFromPath(root, path, includedPropertiesOnly: true, createMissingSubject: null) is
            { } resolved && visibility.IsShown(resolved.Property.Parent)
            ? resolved
            : null;
    }

    /// <summary>
    /// Resolves a subject path from <paramref name="root"/> as browse does. With an
    /// <see cref="McpServerConfiguration.AccessPolicy"/>, the path must resolve as for
    /// <see cref="TryGetReadableProperty(McpServerConfiguration, RegisteredSubject, string)"/> and the subject must be
    /// shown.
    /// </summary>
    /// <returns>The subject, or null when the path does not resolve or something on it is hidden.</returns>
    public static RegisteredSubject? TryGetReadableSubject(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => configuration.TryGetReadableSubject(root, path, McpVisibility.For(configuration, root.Subject));

    /// <summary>
    /// <see cref="TryGetReadableSubject(McpServerConfiguration, RegisteredSubject, string)"/> with the visibility of
    /// the current call, whose root is <paramref name="root"/>.
    /// </summary>
    internal static RegisteredSubject? TryGetReadableSubject(
        this McpServerConfiguration configuration, RegisteredSubject root, string path, McpVisibility visibility)
    {
        if (configuration.AccessPolicy is not { } policy)
        {
            return configuration.ResolutionPathProvider.TryGetSubjectFromPath(root, path);
        }

        var subject = configuration.TryResolveReadableSubject(policy, root, path, out var property);
        return subject is not null && visibility.IsShown(property!.Parent) && visibility.IsShown(subject) ? subject : null;
    }

    /// <summary>
    /// Resolves a subject path through properties the policy allows to read to a subject that is not empty for the
    /// policy, without asking whether a subject is shown.
    /// </summary>
    internal static RegisteredSubject? TryResolveReadableSubject(
        this McpServerConfiguration configuration, IMcpAccessPolicy policy, RegisteredSubject root, string path)
        => configuration.TryResolveReadableSubject(policy, root, path, out _);

    private static RegisteredSubject? TryResolveReadableSubject(
        this McpServerConfiguration configuration, IMcpAccessPolicy policy, RegisteredSubject root, string path,
        out RegisteredSubjectProperty? property)
    {
        property = null;
        if (configuration.ResolutionPathProvider.TryGetPropertyFromPath(root, path, includedPropertiesOnly: true, createMissingSubject: null) is not
            { } resolved)
        {
            return null;
        }

        (property, var index) = resolved;

        var value = property.GetValue();
        var subject = index switch
        {
            null => value as IInterceptorSubject,
            _ when value is null => null,
            int position when property.IsSubjectCollection => SubjectLookup.FindSubjectInCollection(value, position),
            _ when property.IsSubjectDictionary => SubjectLookup.FindSubjectInDictionary(value, index),
            _ => null
        };

        var registered = subject?.TryGetRegisteredSubject();
        return registered is not null && !McpVisibility.IsEmptyForPolicy(registered, policy) ? registered : null;
    }
}
