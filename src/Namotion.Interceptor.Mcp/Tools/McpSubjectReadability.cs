using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether a subject is readable for the tools, for one tool call. Without an access policy every subject is readable.
/// With one, a subject is readable when it is the root, or when it is not empty for the policy and has a readable path:
/// a path from the root, through one of its parents, that passes only properties the configuration's tool path
/// provider effectively includes, which are ones the path provider effectively includes and the policy allows to read.
/// A subject reached only through a link the registry does not track as a parent, such as a [Derived] property that
/// returns it, has no such parent, so with a policy it is not readable, also when the policy allows every read.
/// </summary>
/// <remarks>An instance with a policy caches per subject and is not thread-safe, so it serves one call only.</remarks>
internal sealed class McpSubjectReadability
{
    private readonly IMcpAccessPolicy? _policy;
    private readonly IInterceptorSubject? _rootSubject;
    private readonly PathProviderBase? _pathProvider;
    private readonly PathProviderBase? _toolPathProvider;
    private Dictionary<RegisteredSubject, bool>? _isReadable;
    private ReadableSubjectsPathProvider? _readableSubjectsPathProvider;

    private McpSubjectReadability()
    {
    }

    private McpSubjectReadability(IMcpAccessPolicy policy, IInterceptorSubject rootSubject, McpServerConfiguration configuration)
    {
        _policy = policy;
        _rootSubject = rootSubject;
        _toolPathProvider = configuration.ToolPathProvider;
        _pathProvider = configuration.PathProvider;
    }

    /// <summary>
    /// The readability without a policy, shared by every call: every subject is readable.
    /// </summary>
    public static McpSubjectReadability Unrestricted { get; } = new();

    /// <summary>
    /// The readability for one call from <paramref name="rootSubject"/>. Without a policy it is the shared
    /// <see cref="Unrestricted"/> instance.
    /// </summary>
    public static McpSubjectReadability For(McpServerConfiguration configuration, IInterceptorSubject rootSubject) =>
        configuration.AccessPolicy is { } policy
            ? new McpSubjectReadability(policy, rootSubject, configuration)
            : Unrestricted;

    /// <summary>
    /// The configuration's tool path provider, <paramref name="toolPathProvider"/>, restricted with a policy to
    /// properties whose owner is readable, so a path resolved with it passes readable subjects only. Without a policy,
    /// <paramref name="toolPathProvider"/> itself.
    /// </summary>
    /// <param name="toolPathProvider">The <see cref="McpServerConfiguration.ToolPathProvider"/> of the configuration this readability is for.</param>
    public PathProviderBase GetReadableSubjectsPathProvider(PathProviderBase toolPathProvider) =>
        _policy is null
            ? toolPathProvider
            : _readableSubjectsPathProvider ??= new ReadableSubjectsPathProvider(toolPathProvider, this);

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
        // Attributes are listed only on a listed property, so they make a subject neither readable nor empty.
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
    /// <see cref="IsReadableWithPathCheck(RegisteredSubject)"/> for the registered subject. Always true without a policy;
    /// with one, false for a subject that is not registered.
    /// </summary>
    public bool IsReadableWithPathCheck(IInterceptorSubject subject) =>
        _policy is null || subject.TryGetRegisteredSubject() is { } registered && IsReadableWithPathCheck(registered);

    /// <summary>
    /// Whether the subject is readable: the root always is; any other subject needs a readable path from the root through
    /// one of its parents and must not be empty for the policy. Always true without a policy. The result is cached for
    /// the call, in the cache <see cref="IsReadableWithoutPathCheck"/> shares.
    /// </summary>
    public bool IsReadableWithPathCheck(RegisteredSubject subject)
    {
        if (_policy is null || subject.Subject == _rootSubject)
        {
            return true;
        }

        _isReadable ??= [];
        if (!_isReadable.TryGetValue(subject, out var isReadable))
        {
            isReadable = Evaluate(subject);
            _isReadable[subject] = isReadable;
        }

        return isReadable;
    }

    /// <summary>
    /// Whether the subject is readable, without the path check of
    /// <see cref="IsReadableWithPathCheck(RegisteredSubject)"/>. The caller must already have built the subject's path
    /// with the configuration's <see cref="McpServerConfiguration.ToolPathProvider"/> from the root this readability is
    /// for, as <see cref="McpToolHelper.TryGetSubjectPath"/> does, so only whether the subject is empty for the policy
    /// is checked. Always true without a policy and for the root. The result is cached for the call, in the cache
    /// <see cref="IsReadableWithPathCheck(RegisteredSubject)"/> shares.
    /// </summary>
    public bool IsReadableWithoutPathCheck(RegisteredSubject subject)
    {
        if (_policy is null || subject.Subject == _rootSubject)
        {
            return true;
        }

        // Seeds the cache that IsReadableWithPathCheck reads, as a where filter of the same call may ask about this
        // subject again.
        _isReadable ??= [];
        if (!_isReadable.TryGetValue(subject, out var isReadable))
        {
            isReadable = !IsEmptyForPolicy(subject);
            _isReadable[subject] = isReadable;
        }

        return isReadable;
    }

    // A subject with several parents is readable through any of them, so hiding one link hides only that link. A subject
    // without a readable path from the root is hidden, also when no path reaches the root at all.
    private bool Evaluate(RegisteredSubject subject)
    {
        if (IsEmptyForPolicy(subject))
        {
            return false;
        }

        // The entries of a property are adjacent, and its path does not depend on the key or position.
        RegisteredSubjectProperty? previousProperty = null;
        foreach (var parent in subject.Parents)
        {
            if (parent.Property == previousProperty)
            {
                continue;
            }

            previousProperty = parent.Property;
            if (parent.Property.HasPath(_toolPathProvider!, _rootSubject!))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ReadableSubjectsPathProvider(PathProviderBase inner, McpSubjectReadability readability) : PathProviderBase
    {
        /// <inheritdoc />
        public override long Version => inner.Version;

        public override char PathSeparator => inner.PathSeparator;

        public override char IndexOpen => inner.IndexOpen;

        public override char IndexClose => inner.IndexClose;

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (inner.TryGetPropertySegment(property, out segment) && readability.IsReadableWithPathCheck(property.Parent))
            {
                return true;
            }

            segment = null;
            return false;
        }

        public override bool TryGetPropertyFromSegment(
            RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property)
        {
            // Every property of a subject that is not readable is excluded.
            if (inner.TryGetPropertyFromSegment(subject, segment, out property) && readability.IsReadableWithPathCheck(subject))
            {
                return true;
            }

            property = null;
            return false;
        }
    }
}
