using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Resolves and displays a property value from a path expression.
/// Supports local paths (relative to parent) and global paths (/ prefix).
/// </summary>
[InterceptorSubject]
[ExcludeFromBrowsing]
public partial class RenderExpression : ITitleProvider
{
    private readonly SubjectPathResolver _pathResolver;
    private readonly LatestDerivedValue<object?> _value = new();

    public string Path { get; }
    public MarkdownFile Parent { get; }

    public string? Title => null;

    /// <summary>
    /// The value at <see cref="Path"/>. Resolves the path on every call; render code uses <see cref="GetLastValue"/>.
    /// </summary>
    [Derived]
    public object? Value
    {
        get
        {
            var evaluation = _value.BeginEvaluation();
            return _value.Store(evaluation, ResolveValue());
        }
    }

    /// <summary>
    /// Gets the value <see cref="Value"/> was last evaluated to, read as that property, without resolving
    /// the path again, so that a render tracking scope records the property instead of every folder on
    /// the path. Resolves the path when the property has not been evaluated yet.
    /// </summary>
    public object? GetLastValue()
    {
        return GetPropertyValue(nameof(Value), static subject =>
        {
            var expression = (RenderExpression)subject;
            return expression._value.TryGetValue(out var value) ? value : expression.Value;
        });
    }

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
