using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether the tools show a subject, for one tool call. Without an access policy every subject is shown. With one, a
/// subject is shown when it is the root, or when at least one of its properties is readable and every property on
/// the path the tools write for it is readable.
/// </summary>
internal sealed class McpVisibility
{
    private readonly McpServerConfiguration _configuration;
    private readonly PathProviderBase _pathProvider;
    private readonly RegisteredSubject _root;
    private Dictionary<RegisteredSubject, bool>? _isShown;

    public McpVisibility(McpServerConfiguration configuration, RegisteredSubject root)
    {
        _configuration = configuration;
        _pathProvider = configuration.ToolPathProvider;
        _root = root;
    }

    /// <summary>
    /// Whether the subject has a property, other than an attribute, that the path provider includes and the access
    /// policy allows to read. Always true without a policy.
    /// </summary>
    public bool HasReadableMember(RegisteredSubject subject) =>
        _configuration.AccessPolicy is null || HasReadableMember(subject, _pathProvider);

    internal static bool HasReadableMember(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        // Attributes are listed only on a listed property, so they never make a subject shown on their own.
        foreach (var property in subject.Properties)
        {
            if (!property.IsAttribute && pathProvider.IsPropertyIncluded(property))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the subject is shown. Always true without a policy.
    /// </summary>
    public bool IsShown(RegisteredSubject subject)
    {
        if (_configuration.AccessPolicy is null || subject == _root)
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
        isShown = HasReadableMember(subject, _pathProvider) && IsWrittenPathReadable(subject);
        _isShown[subject] = isShown;
        return isShown;
    }

    private bool IsWrittenPathReadable(RegisteredSubject subject)
    {
        var parents = subject.Parents;
        if (parents.Length == 1)
        {
            // A single parent is the one the written path goes through, so the path is readable when the property
            // holding the subject is, and the path of the subject owning that property is.
            var parent = parents[0];
            return _pathProvider.IsPropertyIncluded(parent.Property) && IsShown(parent.Property.Parent);
        }

        // With several parents the registry picks the chain, so the written path is resolved back. It is written
        // without the configured prefix, which the path reader does not expect.
        var path = McpToolHelper.TryGetSubjectPath(subject, _pathProvider, _root.Subject);
        return path is not null && _configuration.TryGetReadableSubject(_root, path)?.Subject == subject.Subject;
    }
}
