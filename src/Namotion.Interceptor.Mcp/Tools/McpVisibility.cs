using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether the tools show a subject, for one tool call. Without an access policy every subject is shown. With one, a
/// subject is shown when it is the root, or when it is not empty for the policy and every property on the path the
/// tools write for it is one the policy allows to read.
/// </summary>
internal sealed class McpVisibility
{
    private readonly McpServerConfiguration _configuration;
    private readonly IMcpAccessPolicy? _policy;
    private readonly PathProviderBase _pathProvider;
    private readonly RegisteredSubject _root;
    private Dictionary<RegisteredSubject, bool>? _isShown;

    public McpVisibility(McpServerConfiguration configuration, RegisteredSubject root)
    {
        _configuration = configuration;
        _policy = configuration.AccessPolicy;
        _pathProvider = configuration.ResolutionPathProvider;
        _root = root;
    }

    /// <summary>
    /// Whether the subject is empty for the policy: it has at least one property other than an attribute, and the
    /// policy denies read on every one of them. A subject without such properties is not empty.
    /// </summary>
    internal static bool IsEmptyForPolicy(RegisteredSubject subject, IMcpAccessPolicy policy)
    {
        // Attributes are listed only on a listed property, so they make a subject neither shown nor empty.
        var hasProperty = false;
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
            {
                continue;
            }

            if (policy.IsAllowed(property, McpAccessActions.Read))
            {
                return false;
            }

            hasProperty = true;
        }

        return hasProperty;
    }

    /// <summary>
    /// Whether the subject is shown. Always true without a policy.
    /// </summary>
    public bool IsShown(RegisteredSubject subject)
    {
        if (_policy is null || subject == _root)
        {
            return true;
        }

        _isShown ??= [];
        if (_isShown.TryGetValue(subject, out var isShown))
        {
            return isShown;
        }

        // Marked hidden while it is evaluated, so a cycle of parents fails closed instead of recursing.
        _isShown[subject] = false;
        isShown = !IsEmptyForPolicy(subject, _policy) && IsWrittenPathReadable(subject, _policy);
        _isShown[subject] = isShown;
        return isShown;
    }

    private bool IsWrittenPathReadable(RegisteredSubject subject, IMcpAccessPolicy policy)
    {
        var parents = subject.Parents;
        if (parents.Length == 1)
        {
            // A single parent is the one the written path goes through, so the path is readable when the property
            // holding the subject is, and the path of the subject owning that property is.
            var parent = parents[0];
            return McpAccessPathProvider.IsReadable(policy, parent.Property) && IsShown(parent.Property.Parent);
        }

        // With several parents the registry picks the chain, so the written path is resolved back. It is written
        // without the configured prefix, which the path reader does not expect.
        var path = McpToolHelper.TryGetSubjectPath(subject, _pathProvider, _root.Subject);
        return path is not null && _configuration.TryGetReadableSubject(_root, path)?.Subject == subject.Subject;
    }
}
