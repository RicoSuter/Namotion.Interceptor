using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Navigation;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

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
    public MarkdownFile Parent { get; }

    public string? Title => null;

    [Derived]
    public object? Value => ResolveValue();

    public RenderExpression(
        string path,
        MarkdownFile parent,
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
        try
        {
            IInterceptorSubject? root;

            var path = Path;
            if (path.StartsWith(RootPathPrefix))
            {
                path = path[RootPathPrefix.Length..];
                root = _rootManager.Root;
            }
            else
            {
                var index = path.IndexOf('.');
                var key = path.Substring(0, index);
                path = path.Substring(index + 1);
                root = Parent.Children[key];
            }

            return root != null ? _pathResolver.ResolveValue(root, path) : null;
        }
        catch
        {
            // TODO: Make exception free and remove try-catch
            return null;
        }
    }
}
