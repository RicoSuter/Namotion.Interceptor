using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether the tools show a subject, for one tool call. Without an access policy every subject is shown. With one, a
/// subject is shown when it is the root, or when it is not empty for the policy and, through at least one of its
/// parents, no path reaches the root or one reaches it through properties the policy allows to read only.
/// </summary>
/// <remarks>An instance with a policy caches per subject and is not thread-safe, so it serves one call only.</remarks>
internal sealed class McpVisibility
{
    private readonly IMcpAccessPolicy? _policy;
    private readonly IInterceptorSubject? _rootSubject;
    private readonly PathProviderBase? _readablePathProvider;
    private Dictionary<RegisteredSubject, bool>? _isShown;
    private ShownSubjectsPathProvider? _listingPathProvider;
    private ShownSubjectsPathProvider? _resolutionPathProvider;

    private McpVisibility()
    {
    }

    // readablePathProvider includes exactly what the policy allows to read.
    private McpVisibility(IMcpAccessPolicy policy, IInterceptorSubject rootSubject, PathProviderBase readablePathProvider)
    {
        _policy = policy;
        _rootSubject = rootSubject;
        _readablePathProvider = readablePathProvider;
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
        configuration.AccessPolicy is { } policy
            ? new McpVisibility(policy, rootSubject, configuration.ResolutionPathProvider)
            : Unrestricted;

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

    // A subject with several parents is shown through any of them, so hiding one link hides only that link. A parent
    // from which no path reaches the root hides nothing, so a subject without such a path is shown when not empty.
    private bool Evaluate(RegisteredSubject subject, IMcpAccessPolicy policy)
    {
        var parents = subject.Parents;
        if (parents.Length == 0 || IsEmptyForPolicy(subject, policy))
        {
            return false;
        }

        foreach (var parent in parents)
        {
            if (!parent.Property.IsAnyPropertyOnPathExcluded(_readablePathProvider!, _rootSubject, parent.Index))
            {
                return true;
            }
        }

        return false;
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
