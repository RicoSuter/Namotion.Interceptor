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
    /// read, an attribute together with its property, else the path reads as missing.
    /// </summary>
    /// <returns>The property and the key of its last segment, as <see cref="PathExtensions.TryGetPropertyFromPath(PathProviderBase, RegisteredSubject, string)"/> returns them, or null.</returns>
    public static (RegisteredSubjectProperty Property, object? Index)? TryGetReadableProperty(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => configuration.ResolutionPathProvider.TryGetPropertyFromPath(
            root, path, includedPropertiesOnly: configuration.AccessPolicy is not null, createMissingSubject: null);

    /// <summary>
    /// Resolves a subject path from <paramref name="root"/> as browse does. With an
    /// <see cref="McpServerConfiguration.AccessPolicy"/>, every property on the path must be readable, as for
    /// <see cref="TryGetReadableProperty"/>, and the subject must not be empty for the policy: it has a property other
    /// than an attribute, and the policy denies read on every one of them.
    /// </summary>
    /// <returns>The subject, or null when the path does not resolve or something on it is hidden.</returns>
    public static RegisteredSubject? TryGetReadableSubject(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => configuration.AccessPolicy is not { } policy
            ? configuration.ResolutionPathProvider.TryGetSubjectFromPath(root, path)
            : configuration.TryResolveReadableSubject(policy, root, path);

    /// <summary>
    /// Resolves a subject path through properties the policy allows to read to a subject that is not empty for the
    /// policy, without asking whether a subject is shown.
    /// </summary>
    internal static RegisteredSubject? TryResolveReadableSubject(
        this McpServerConfiguration configuration, IMcpAccessPolicy policy, RegisteredSubject root, string path)
    {
        if (configuration.ResolutionPathProvider.TryGetPropertyFromPath(root, path, includedPropertiesOnly: true, createMissingSubject: null) is not
            { } resolved)
        {
            return null;
        }

        var (property, index) = resolved;

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
