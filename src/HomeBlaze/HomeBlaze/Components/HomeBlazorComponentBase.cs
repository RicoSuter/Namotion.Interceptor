using HomeBlaze.Core.Services;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Blazor;

namespace HomeBlaze.Components;

/// <summary>
/// Base class for HomeBlaze Blazor components that automatically
/// subscribes to property changes on the root subject.
/// </summary>
public abstract class HomeBlazorComponentBase : ContextTrackingComponentBase
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
