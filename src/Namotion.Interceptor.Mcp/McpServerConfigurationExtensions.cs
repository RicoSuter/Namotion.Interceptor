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
            .GetPathProvider(configuration.ToolPathProvider)
            .TryGetPropertyFromPath(root, path);

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
    /// Writes the path of <paramref name="subject"/> from <paramref name="root"/> as search lists it, and browse as its
    /// <c>$path</c>: after <see cref="McpServerConfiguration.PathPrefix"/>, through the first parent, in parent order,
    /// from which the path can be written; for <paramref name="root"/> itself the prefix, or null when the prefix is
    /// empty. Null when no path can be written, the <see cref="McpServerConfiguration.AccessPolicy"/> does not show the
    /// subject, see "Access Policy" in the MCP documentation, or the subject's type is in
    /// <see cref="McpServerConfiguration.ExcludeTypes"/> or derived from one, so that a subject gets a path exactly when
    /// search can list it. The counterpart of
    /// <see cref="TryGetReadableSubject(McpServerConfiguration, RegisteredSubject, string)"/>, which resolves a path this
    /// returns for a subject other than <paramref name="root"/> back to the subject.
    /// </summary>
    /// <returns>The path, or null.</returns>
    public static string? TryGetSubjectPath(
        this McpServerConfiguration configuration, RegisteredSubject root, RegisteredSubject subject)
    {
        if (McpToolHelper.ShouldExcludeByType(subject, configuration.ExcludeTypes, requestExcludeTypes: null))
        {
            return null;
        }

        var path = McpToolHelper.TryGetSubjectPath(subject, configuration, root.Subject);

        // A written path is a readable one, so whether the subject is empty for the policy is left, as search checks it.
        return path is not null &&
            configuration.AccessPolicy is { } policy &&
            subject.Subject != root.Subject &&
            McpVisibility.IsEmptyForPolicy(policy, (PathProviderBase)configuration.PathProvider, subject)
                ? null
                : path;
    }

    /// <summary>
    /// <see cref="TryGetReadableSubject(McpServerConfiguration, RegisteredSubject, string)"/> with the visibility of
    /// the current call, whose root is <paramref name="root"/>.
    /// </summary>
    internal static RegisteredSubject? TryGetReadableSubject(
        this McpServerConfiguration configuration, RegisteredSubject root, string path, McpVisibility visibility)
    {
        if (configuration.AccessPolicy is null)
        {
            return configuration.ToolPathProvider.TryGetSubjectFromPath(root, path);
        }

        var pathProvider = visibility.GetPathProvider(configuration.ToolPathProvider);
        var subject = TryResolveReadableSubject(pathProvider, visibility, root, path);
        return subject is not null && visibility.IsShown(subject) ? subject : null;
    }

    // Resolves through the properties the provider includes to a subject that is not empty for the policy.
    private static RegisteredSubject? TryResolveReadableSubject(
        PathProviderBase pathProvider, McpVisibility visibility, RegisteredSubject root, string path)
    {
        if (pathProvider.TryGetPropertyFromPath(root, path) is not
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
        return registered is not null && !visibility.IsEmptyForPolicy(registered) ? registered : null;
    }
}
