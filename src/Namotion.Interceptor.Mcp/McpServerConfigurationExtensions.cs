using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Access checks of a tool configuration, for the tools of this package and of an application alike.
/// </summary>
public static class McpServerConfigurationExtensions
{
    /// <summary>
    /// Whether <see cref="McpServerConfiguration.AccessPolicy"/> allows the action on the member.
    /// </summary>
    public static bool IsAllowed(this McpServerConfiguration configuration, RegisteredSubjectProperty member, string action)
        => configuration.AccessPolicy.IsAllowed(member, action);

    /// <summary>
    /// Gets <paramref name="value"/>, a value of <paramref name="member"/>, after the
    /// <see cref="McpServerConfiguration.ValueFilters"/>, applied in order: the value the built-in tools return for the
    /// member. A tool of the application calls it for a member value it returns or compares, so that it shows what the
    /// built-in tools show. Call it only for a member the client may read.
    /// </summary>
    public static object? ApplyValueFilters(this McpServerConfiguration configuration, RegisteredSubjectProperty member, object? value)
        => McpToolHelper.ApplyValueFilters(member, value, configuration.ValueFilters);

    /// <summary>
    /// Resolves a property path from <paramref name="root"/> as get_property does: null when the path does not resolve
    /// or the <see cref="McpServerConfiguration.AccessPolicy"/> hides something on it, see "Access Policy" in the MCP
    /// documentation. <paramref name="root"/> is the tool root: it is readable, and the path is checked from it.
    /// </summary>
    /// <returns>The property and the key of its last segment, as <see cref="PathExtensions.TryGetPropertyFromPath(PathProviderBase, RegisteredSubject, string)"/> returns them, or null.</returns>
    public static (RegisteredSubjectProperty Property, object? Index)? TryGetReadableProperty(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => McpSubjectReadability.For(configuration, root.Subject)
            .GetReadableSubjectsPathProvider(configuration.ToolPathProvider)
            .TryGetPropertyFromPath(root, path);

    /// <summary>
    /// Resolves a subject path from <paramref name="root"/> as browse does: null when the path does not resolve or the
    /// <see cref="McpServerConfiguration.AccessPolicy"/> hides the subject or something on the path to it, see "Access
    /// Policy" in the MCP documentation. <paramref name="root"/> is the tool root: it is readable, and the path is
    /// checked from it.
    /// </summary>
    /// <returns>The subject, or null.</returns>
    public static RegisteredSubject? TryGetReadableSubject(
        this McpServerConfiguration configuration, RegisteredSubject root, string path)
        => configuration.TryGetReadableSubject(root, path, McpSubjectReadability.For(configuration, root.Subject));

    /// <summary>
    /// Builds the path of <paramref name="subject"/> from <paramref name="root"/> that search lists, which is also the
    /// <c>$path</c> browse returns for a subject it lists: after <see cref="McpServerConfiguration.PathPrefix"/>, through
    /// the first parent, in parent order, from which the path can be built; for <paramref name="root"/> itself the
    /// prefix, or null when the prefix is empty. Null when no path can be built, the <see cref="McpServerConfiguration.AccessPolicy"/> hides the
    /// subject, see "Access Policy" in the MCP documentation, or the subject's type is in
    /// <see cref="McpServerConfiguration.ExcludeTypes"/> or derived from one, so that a subject gets a path exactly when
    /// search can list it. <paramref name="root"/> is the tool root: it is readable, and the path is built from it. The
    /// counterpart of <see cref="TryGetReadableSubject(McpServerConfiguration, RegisteredSubject, string)"/>, which
    /// resolves a path this returns for a subject other than <paramref name="root"/> back to the subject when the path
    /// provider resolves the path with the prefix, as without a prefix or with the prefix "/" and the separator "/".
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

        // The built path passes only readable properties, so only emptiness is left to check, as search does.
        return path is not null &&
            configuration.ReadPolicy is { } policy &&
            subject.Subject != root.Subject &&
            McpSubjectReadability.IsEmptyForPolicy(policy, configuration.PathProvider, subject)
                ? null
                : path;
    }

    /// <summary>
    /// <see cref="TryGetReadableSubject(McpServerConfiguration, RegisteredSubject, string)"/> with the readability of
    /// the current call, whose root is <paramref name="root"/>.
    /// </summary>
    internal static RegisteredSubject? TryGetReadableSubject(
        this McpServerConfiguration configuration, RegisteredSubject root, string path, McpSubjectReadability readability)
        => readability.GetReadableSubjectsPathProvider(configuration.ToolPathProvider).TryGetSubjectFromPath(root, path) is { } subject &&
            readability.IsReadableWithPathCheck(subject)
                ? subject
                : null;
}
