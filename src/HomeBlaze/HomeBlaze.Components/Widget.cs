using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Components;

/// <summary>
/// A subject that references another subject by path and renders its widget component.
/// Enables embedding widgets by reference in markdown instead of defining them inline.
/// </summary>
[Category("Dashboard")]
[Description("References another subject by path and renders its widget")]
[DisplayName("Widget")]
[SubjectIcon("Widgets")]
[InterceptorSubject]
public partial class Widget : ITitleProvider, IConfigurable
{
    private const int MaximumSingleParentDepth = 64;

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

    /// <summary>
    /// Whether a subject is this widget or contains it at any depth. Showing such a subject inside the
    /// widget would show the widget again without end.
    /// </summary>
    /// <remarks>
    /// Only containment counts. A widget is not a parent of the subject it shows, so widgets that show
    /// the container of each other are not found here and need a check where they are rendered.
    /// </remarks>
    public bool IsSelfOrAncestor(IInterceptorSubject subject)
    {
        // The usual chain has one parent per subject and is walked without an allocation. The search over
        // all parents allocates, so it only runs when the chain branches or is longer than any real nesting.
        IInterceptorSubject current = this;
        for (var depth = 0; depth < MaximumSingleParentDepth; depth++)
        {
            if (ReferenceEquals(current, subject))
            {
                return true;
            }

            var parents = current.GetParents();
            if (parents.Length == 0)
            {
                return false;
            }

            if (parents.Length > 1)
            {
                break;
            }

            current = parents[0].Property.Subject;
        }

        return IsSelfOrAncestorInAnyBranch(current, subject);
    }

    private static bool IsSelfOrAncestorInAnyBranch(IInterceptorSubject start, IInterceptorSubject subject)
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(start);

        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (ReferenceEquals(current, subject))
            {
                return true;
            }

            foreach (var parent in current.GetParents())
            {
                queue.Enqueue(parent.Property.Subject);
            }
        }

        return false;
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
