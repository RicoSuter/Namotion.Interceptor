using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Navigation;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Widgets;

/// <summary>
/// Resolves and displays a property value from a path expression.
/// Supports local paths (relative to parent) and global paths (Root. prefix).
/// </summary>
[InterceptorSubject]
public partial class RenderExpression : ITitleProvider
{
    private const string RootPathPrefix = "Root.";

    private readonly SubjectPathResolver _pathResolver;
    private readonly RootManager _rootManager;

    public string Path { get; }
    public IInterceptorSubject Parent { get; }

    public string? Title => null;

    [Derived]
    public object? Value => ResolveValue();

    public RenderExpression(
        string path,
        IInterceptorSubject parent,
        SubjectPathResolver pathResolver,
        RootManager rootManager)
    {
        Path = path;
        Parent = parent;
        _pathResolver = pathResolver;
        _rootManager = rootManager;
    }

    private object? ResolveValue()
    {
        IInterceptorSubject? root;
        var path = Path;

        if (path.StartsWith(RootPathPrefix))
        {
            path = path.Substring(RootPathPrefix.Length);
            root = _rootManager.Root;
        }
        else
        {
            root = Parent;
        }

        if (root == null)
            return null;

        return _pathResolver.ResolveValue(root, path);
    }
}
