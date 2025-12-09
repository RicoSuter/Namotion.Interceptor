using HomeBlaze.Core;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Blazor;

namespace HomeBlaze.Components;

/// <summary>
/// Base class for HomeBlaze Blazor components that automatically
/// tracks property reads and only re-renders when those properties change.
/// </summary>
public abstract class HomeBlazorComponentBase : TrackingComponentBase
{
    [Inject]
    protected RootManager RootManager { get; set; } = null!;

    /// <summary>
    /// The root subject. Available after OnInitialized.
    /// </summary>
    protected IInterceptorSubject? Root => RootManager.Root;

    /// <summary>
    /// The root subject's context. Available after OnInitialized.
    /// </summary>
    protected IInterceptorSubjectContext? RootContext => Root?.Context;

    /// <summary>
    /// The tracking context from the root subject.
    /// </summary>
    protected override IInterceptorSubjectContext? TrackingContext => RootContext;
}
