using System.Runtime.InteropServices;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Whether the tools show a subject, for one tool call. Without an access policy every subject is shown. With one, a
/// subject is shown when it is the root, or when it is not empty for the policy and every property on the path the
/// tools write for it is one the policy allows to read.
/// </summary>
/// <remarks>An instance with a policy caches per subject and is not thread-safe, so it serves one call only.</remarks>
internal sealed class McpVisibility
{

    private readonly McpServerConfiguration? _configuration;
    private readonly IMcpAccessPolicy? _policy;
    private readonly IInterceptorSubject? _rootSubject;
    private RegisteredSubject? _root;
    private Dictionary<RegisteredSubject, bool>? _isShown;
    private List<RegisteredSubject>? _chain;

    private McpVisibility()
    {
    }

    private McpVisibility(McpServerConfiguration configuration, IMcpAccessPolicy policy, IInterceptorSubject rootSubject)
    {
        _configuration = configuration;
        _policy = policy;
        _rootSubject = rootSubject;
    }

    /// <summary>
    /// The visibility without a policy, shared by every call: every subject is shown.
    /// </summary>
    public static McpVisibility Unrestricted { get; } = new();

    /// <summary>
    /// The visibility for one call from <paramref name="rootSubject"/>. Without a policy it is a shared instance that
    /// shows every subject; with one, the registered root is looked up only when a path has to be resolved back.
    /// </summary>
    public static McpVisibility For(McpServerConfiguration configuration, IInterceptorSubject rootSubject) =>
        configuration.AccessPolicy is { } policy ? new McpVisibility(configuration, policy, rootSubject) : Unrestricted;

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
        return _isShown.TryGetValue(subject, out var isShown) ? isShown : EvaluateChain(subject, _policy, _isShown);
    }

    // Walks up while every subject has a single parent: that parent is the one the written path goes through, so a
    // subject is shown when it is not empty, the property holding it is readable and its owner is shown. The walk ends
    // at the root, at a subject already decided, at a failed check or at a subject with several parents, and its
    // result holds for every subject walked, whose own checks passed. Each one is marked hidden while it is evaluated,
    // so a cycle of parents fails closed.
    private bool EvaluateChain(RegisteredSubject subject, IMcpAccessPolicy policy, Dictionary<RegisteredSubject, bool> isShownBySubject)
    {
        var chain = _chain ??= [];
        var current = subject;
        bool isShown;
        while (true)
        {
            if (current.Subject == _rootSubject)
            {
                isShown = true;
                break;
            }

            if (isShownBySubject.TryGetValue(current, out isShown))
            {
                break;
            }

            isShownBySubject[current] = false;
            chain.Add(current);
            if (IsEmptyForPolicy(current, policy))
            {
                isShown = false;
                break;
            }

            var parents = current.Parents;
            if (parents.Length != 1)
            {
                isShown = IsWrittenPathResolvedBack(current, policy);
                break;
            }

            var parent = parents[0];
            if (!McpAccessPathProvider.IsReadable(policy, parent.Property))
            {
                isShown = false;
                break;
            }

            current = parent.Property.Parent;
        }

        foreach (var walked in CollectionsMarshal.AsSpan(chain))
        {
            isShownBySubject[walked] = isShown;
        }

        chain.Clear();
        return isShown;
    }

    // With several parents the registry picks the chain, so the written path is resolved back through readable
    // properties. It is written without the configured prefix, which the path reader does not expect. The resolver
    // does not ask whether subjects are shown, so this never recurses into the walk.
    private bool IsWrittenPathResolvedBack(RegisteredSubject subject, IMcpAccessPolicy policy)
    {
        var configuration = _configuration!;
        var path = McpToolHelper.TryGetSubjectPath(subject, configuration.ResolutionPathProvider, _rootSubject!);
        if (path is null)
        {
            return false;
        }

        _root ??= _rootSubject!.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");
        return configuration.TryResolveReadableSubject(policy, _root, path)?.Subject == subject.Subject;
    }
}
