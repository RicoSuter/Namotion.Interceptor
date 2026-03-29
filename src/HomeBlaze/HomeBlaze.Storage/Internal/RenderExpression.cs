using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Resolves and displays a property value from a path expression.
/// Supports local paths (relative to parent) and global paths (/ prefix).
/// </summary>
[InterceptorSubject]
public partial class RenderExpression : ITitleProvider
{
    private readonly SubjectPathResolver _pathResolver;

    public string Path { get; }
    public MarkdownFile Parent { get; }

    public string? Title => null;

    [Derived]
    public object? Value => ResolveValue();

    public RenderExpression(
        string path,
        MarkdownFile parent,
        SubjectPathResolver pathResolver)
    {
        Path = path;
        Parent = parent;
        _pathResolver = pathResolver;
    }

    private object? ResolveValue()
    {
        try
        {
            return _pathResolver.ResolveValue(Path, PathStyle.Canonical, relativeTo: Parent);
        }
        catch
        {
            return null;
        }
    }
}
