using System.Runtime.InteropServices;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether the tools show a subject, for one tool call. Without an access policy every subject is shown. With one, a
/// subject is shown when it is the root, or when it is not empty for the policy and every property on the path the
/// tools write for it is one the policy allows to read.
/// </summary>
/// <remarks>An instance with a policy caches per subject and is not thread-safe, so it serves one call only.</remarks>
internal sealed class McpVisibility
{
    private readonly IMcpAccessPolicy? _policy;
    private readonly IInterceptorSubject? _rootSubject;
    private Dictionary<RegisteredSubject, bool>? _isShown;
    private List<(RegisteredSubjectProperty Property, object? Index)>? _pathProperties;
    private ShownSubjectsPathProvider? _listingPathProvider;
    private ShownSubjectsPathProvider? _resolutionPathProvider;

    private McpVisibility()
    {
    }

    private McpVisibility(IMcpAccessPolicy policy, IInterceptorSubject rootSubject)
    {
        _policy = policy;
        _rootSubject = rootSubject;
    }

    /// <summary>
    /// The visibility without a policy, shared by every call: every subject is shown.
    /// </summary>
    public static McpVisibility Unrestricted { get; } = new();

    /// <summary>
    /// The visibility for one call from <paramref name="rootSubject"/>. Without a policy it is the shared
    /// <see cref="Unrestricted"/> instance.
    /// </summary>
    public static McpVisibility For(McpServerConfiguration configuration, IInterceptorSubject rootSubject) =>
        configuration.AccessPolicy is { } policy ? new McpVisibility(policy, rootSubject) : Unrestricted;

    /// <summary>
    /// The configuration's listing provider, <paramref name="listingPathProvider"/>, restricted with a policy to
    /// properties whose owner is shown, so a path resolved with it passes shown subjects
    /// only. Without a policy, <paramref name="listingPathProvider"/> itself.
    /// </summary>
    public PathProviderBase GetListingPathProvider(PathProviderBase listingPathProvider) =>
        _policy is null ? listingPathProvider : GetShownSubjectsPathProvider(ref _listingPathProvider, listingPathProvider);

    /// <summary>
    /// The configuration's resolution provider, <paramref name="resolutionPathProvider"/>, restricted as
    /// <see cref="GetListingPathProvider"/> restricts the listing provider.
    /// </summary>
    public PathProviderBase GetResolutionPathProvider(PathProviderBase resolutionPathProvider) =>
        _policy is null ? resolutionPathProvider : GetShownSubjectsPathProvider(ref _resolutionPathProvider, resolutionPathProvider);

    // The cache is kept per inner provider, so a call with another provider never gets one wrapping the first.
    private ShownSubjectsPathProvider GetShownSubjectsPathProvider(ref ShownSubjectsPathProvider? cached, PathProviderBase inner) =>
        cached is not null && cached.Inner == inner ? cached : cached = new ShownSubjectsPathProvider(inner, this);

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
    /// Whether the subject is shown. Always true without a policy; with one, false for a subject that is not
    /// registered.
    /// </summary>
    public bool IsShown(IInterceptorSubject subject) =>
        _policy is null || subject.TryGetRegisteredSubject() is { } registered && IsShown(registered);

    /// <summary>
    /// Whether the subject is shown. Always true without a policy.
    /// </summary>
    public bool IsShown(RegisteredSubject subject)
    {
        if (_policy is null || subject.Subject == _rootSubject)
        {
            return true;
        }

        _isShown ??= [];
        if (!_isShown.TryGetValue(subject, out var isShown))
        {
            isShown = Evaluate(subject, _policy);
            _isShown[subject] = isShown;
        }

        return isShown;
    }

    // The properties checked are exactly the chain the tools write the subject's path through, so a subject's own
    // check never relies on an owner's, whose path may go through other parents. A subject the registry writes no
    // path for has no path to check.
    private bool Evaluate(RegisteredSubject subject, IMcpAccessPolicy policy)
    {
        var parents = subject.Parents;
        if (parents.Length == 0 || IsEmptyForPolicy(subject, policy))
        {
            return false;
        }

        var parent = parents[0];
        var pathProperties = _pathProperties ??= [];
        if (!parent.Property.TryGetPathProperties(_rootSubject, parent.Index, pathProperties))
        {
            return true;
        }

        var isReadable = true;
        foreach (var (property, _) in CollectionsMarshal.AsSpan(pathProperties))
        {
            if (!McpAccessPathProvider.IsReadable(policy, property))
            {
                isReadable = false;
                break;
            }
        }

        // The entries pin their subjects, so the reused list is not kept filled.
        pathProperties.Clear();
        return isReadable;
    }

    private sealed class ShownSubjectsPathProvider(PathProviderBase inner, McpVisibility visibility) : PathProviderBase
    {
        public PathProviderBase Inner => inner;

        public override char PathSeparator => inner.PathSeparator;

        public override char IndexOpen => inner.IndexOpen;

        public override char IndexClose => inner.IndexClose;

        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
            inner.IsPropertyIncluded(property) && visibility.IsShown(property.Parent);

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property) =>
            inner.TryGetPropertySegment(property);

        public override RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment) =>
            inner.TryGetPropertyFromSegment(subject, segment);
    }
}
