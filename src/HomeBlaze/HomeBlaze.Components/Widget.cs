using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A subject that references another subject by path and renders its widget component.
/// Enables embedding widgets by reference in markdown instead of defining them inline.
/// </summary>
[Category("Components")]
[Description("References another subject by path and renders its widget")]
[InterceptorSubject]
public partial class Widget : ITitleProvider, IConfigurable
{
    private readonly SubjectPathResolver _pathResolver;
    private readonly LatestDerivedValue<IInterceptorSubject?> _resolvedSubject = new();

    /// <summary>
    /// Path to the subject to render.
    /// Supports "/folder/file.json" (with [InlinePaths] attribute) for absolute paths from root.
    /// </summary>
    [Configuration]
    public partial string Path { get; set; }

    public string? Title => null;

    /// <summary>
    /// The resolved subject from the path. Null if path is invalid or subject not found.
    /// Resolves the path on every call; render code uses <see cref="GetLastResolvedSubject"/>.
    /// </summary>
    [Derived]
    public IInterceptorSubject? ResolvedSubject
    {
        get
        {
            var evaluation = _resolvedSubject.BeginEvaluation();
            return _resolvedSubject.Store(evaluation, ResolveSubject());
        }
    }

    /// <summary>
    /// Gets the value <see cref="ResolvedSubject"/> was last evaluated to, read as that property, without
    /// resolving the path again, so that a render tracking scope records the property instead of every
    /// folder on the path. Resolves the path when the property has not been evaluated yet.
    /// </summary>
    public IInterceptorSubject? GetLastResolvedSubject()
    {
        return GetPropertyValue(nameof(ResolvedSubject), static subject =>
        {
            var widget = (Widget)subject;
            return widget._resolvedSubject.TryGetValue(out var resolvedSubject) ? resolvedSubject : widget.ResolvedSubject;
        });
    }

    public Widget(SubjectPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
        Path = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private IInterceptorSubject? ResolveSubject()
    {
        if (string.IsNullOrEmpty(Path))
            return null;

        try
        {
            return _pathResolver.ResolveSubject(Path, PathStyle.Canonical, relativeTo: this);
        }
        catch
        {
            return null;
        }
    }
}
