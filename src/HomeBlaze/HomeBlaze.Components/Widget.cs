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

    /// <summary>
    /// Path to the subject to render.
    /// Supports "/folder/file.json" (with [InlinePaths] attribute) for absolute paths from root.
    /// A relative path resolves from the page that contains the widget, where its first segment can be
    /// the name of a block on that page, or from the widget itself when it is not on a page.
    /// </summary>
    [Configuration]
    public partial string Path { get; set; }

    public string? Title => null;

    /// <summary>
    /// The resolved subject from the path. Null if path is invalid or subject not found.
    /// </summary>
    [Derived]
    public IInterceptorSubject? ResolvedSubject => ResolveSubject();

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
            return _pathResolver.ResolveScopedSubject(Path, PathStyle.Canonical, this);
        }
        catch
        {
            return null;
        }
    }
}
