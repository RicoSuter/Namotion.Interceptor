using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether the tools show a subject, for one tool call. Without an access policy every subject is shown. With one, a
/// subject is shown when it is the root, or when it is not empty for the policy and has a readable path: a path from the
/// root, through one of its parents, that passes only properties the configuration's tool path provider effectively
/// includes, which are ones the path provider effectively includes and the policy allows to read.
/// </summary>
/// <remarks>An instance with a policy caches per subject and is not thread-safe, so it serves one call only.</remarks>
internal sealed class McpVisibility
{
    private readonly IMcpAccessPolicy? _policy;
    private readonly IInterceptorSubject? _rootSubject;
    private readonly PathProviderBase? _pathProvider;
    private readonly PathProviderBase? _toolPathProvider;
    private Dictionary<RegisteredSubject, bool>? _isShown;
    private ShownSubjectsPathProvider? _shownSubjectsPathProvider;

    private McpVisibility()
    {
    }

    private McpVisibility(IMcpAccessPolicy policy, IInterceptorSubject rootSubject, McpServerConfiguration configuration)
    {
        _policy = policy;
        _rootSubject = rootSubject;
        _toolPathProvider = configuration.ToolPathProvider;
        _pathProvider = configuration.PathProvider;
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
            ? new McpVisibility(policy, rootSubject, configuration)
            : Unrestricted;

    /// <summary>
    /// The configuration's tool path provider, <paramref name="toolPathProvider"/>, restricted with a policy to
    /// properties whose owner is shown, so a path resolved with it passes shown subjects only. Without a policy,
    /// <paramref name="toolPathProvider"/> itself.
    /// </summary>
    /// <param name="toolPathProvider">The <see cref="McpServerConfiguration.ToolPathProvider"/> of the configuration this visibility is for.</param>
    public PathProviderBase GetPathProvider(PathProviderBase toolPathProvider) =>
        _policy is null
            ? toolPathProvider
            : _shownSubjectsPathProvider ??= new ShownSubjectsPathProvider(toolPathProvider, this);

    /// <summary>
    /// Whether the subject is empty for the policy: it has at least one property other than an attribute that the
    /// configuration's path provider effectively includes, and the policy denies read on every one of them. A subject
    /// without such properties is not empty. Always false without a policy.
    /// </summary>
    internal bool IsEmptyForPolicy(RegisteredSubject subject) =>
        _policy is not null && IsEmptyForPolicy(_policy, _pathProvider!, subject);

    /// <summary>
    /// Whether the subject is empty for <paramref name="policy"/>, see <see cref="IsEmptyForPolicy(RegisteredSubject)"/>.
    /// </summary>
    /// <param name="policy">The access policy.</param>
    /// <param name="pathProvider">The configuration's <see cref="McpServerConfiguration.PathProvider"/>.</param>
    /// <param name="subject">The subject.</param>
    internal static bool IsEmptyForPolicy(IMcpAccessPolicy policy, PathProviderBase pathProvider, RegisteredSubject subject)
    {
        // Attributes are listed only on a listed property, so they make a subject neither shown nor empty.
        var hasProperty = false;
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || !pathProvider.IsPropertyEffectivelyIncluded(property))
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
            isShown = Evaluate(subject);
            _isShown[subject] = isShown;
        }

        return isShown;
    }

    /// <summary>
    /// <see cref="IsShown(RegisteredSubject)"/> for a subject whose path <see cref="McpToolHelper.TryGetSubjectPath"/>
    /// has written from the root this visibility is for, which is a readable path, so only whether the subject is empty
    /// for the policy is left to check.
    /// </summary>
    public bool IsShownWithWrittenPath(RegisteredSubject subject)
    {
        if (_policy is null || subject.Subject == _rootSubject)
        {
            return true;
        }

        // Seeds the cache that IsShown reads, as a where filter of the same call may ask about this subject again.
        _isShown ??= [];
        if (!_isShown.TryGetValue(subject, out var isShown))
        {
            isShown = !IsEmptyForPolicy(subject);
            _isShown[subject] = isShown;
        }

        return isShown;
    }

    // A subject with several parents is shown through any of them, so hiding one link hides only that link. A subject
    // without a readable path from the root is not shown, also when no path reaches the root at all.
    private bool Evaluate(RegisteredSubject subject)
    {
        if (IsEmptyForPolicy(subject))
        {
            return false;
        }

        foreach (var parent in subject.Parents)
        {
            if (parent.Property.HasPath(_toolPathProvider!, _rootSubject!))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ShownSubjectsPathProvider(PathProviderBase inner, McpVisibility visibility) : PathProviderBase
    {
        public override char PathSeparator => inner.PathSeparator;

        public override char IndexOpen => inner.IndexOpen;

        public override char IndexClose => inner.IndexClose;

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (inner.TryGetPropertySegment(property, out segment) && visibility.IsShown(property.Parent))
            {
                return true;
            }

            segment = null;
            return false;
        }

        public override bool TryGetPropertyFromSegment(
            RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property)
        {
            // Every property of a subject that is not shown is excluded.
            if (inner.TryGetPropertyFromSegment(subject, segment, out property) && visibility.IsShown(subject))
            {
                return true;
            }

            property = null;
            return false;
        }
    }
}
