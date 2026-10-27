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
    /// Resolves a property path from <paramref name="root"/> as get_property does: null when the path does not resolve
    /// or the <see cref="McpServerConfiguration.AccessPolicy"/> hides something on it, see "Access Policy" in the MCP
    /// documentation.
    /// </summary>
    /// <returns>The property and the key of its last segment, as <see cref="PathExtensions.TryGetPropertyFromPath(PathProviderBase, RegisteredSubject, string)"/> returns them, or null.</returns>
    public static (RegisteredSubjectProperty Property, object? Index)? TryGetReadableProperty(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => McpVisibility.For(configuration, root.Subject)
            .GetResolutionPathProvider(configuration.ResolutionPathProvider)
            .TryGetPropertyFromPath(root, path, includedPropertiesOnly: configuration.AccessPolicy is not null, createMissingSubject: null);

    /// <summary>
    /// Resolves a subject path from <paramref name="root"/> as browse does: null when the path does not resolve or the
    /// <see cref="McpServerConfiguration.AccessPolicy"/> hides the subject or something on the path to it, see "Access
    /// Policy" in the MCP documentation.
    /// </summary>
    /// <returns>The subject, or null.</returns>
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

        var pathProvider = visibility.GetResolutionPathProvider(configuration.ResolutionPathProvider);
        var subject = TryResolveReadableSubject(pathProvider, policy, root, path);
        return subject is not null && visibility.IsShown(subject) ? subject : null;
    }

    // Resolves through the properties the provider includes to a subject that is not empty for the policy.
    private static RegisteredSubject? TryResolveReadableSubject(
        PathProviderBase pathProvider, IMcpAccessPolicy policy, RegisteredSubject root, string path)
    {
        if (pathProvider.TryGetPropertyFromPath(root, path, includedPropertiesOnly: true, createMissingSubject: null) is not
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
