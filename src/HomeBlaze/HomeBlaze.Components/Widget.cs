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
[InterceptorSubject]
public partial class Widget : ITitleProvider, IConfigurableSubject
{
    private readonly SubjectPathResolver _pathResolver;

    /// <summary>
    /// Path to the subject to render.
    /// Supports "Root.folder.file.json" (with [Children] attribute) for absolute paths from root.
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
            return _pathResolver.ResolveFromRelativePath(Path);
        }
        catch
        {
            return null;
        }
    }
}
