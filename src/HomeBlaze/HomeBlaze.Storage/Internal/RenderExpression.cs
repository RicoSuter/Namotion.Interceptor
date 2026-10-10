using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Resolves and displays a property value from a path expression.
/// A path with a / prefix resolves from the root. Every other path resolves from the page, and its
/// first segment can be the name of a block on that page.
/// </summary>
[InterceptorSubject]
[ExcludeFromBrowsing]
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
            return _pathResolver.ResolveScopedValue(Path, PathStyle.Canonical, Parent);
        }
        catch
        {
            return null;
        }
    }
}
