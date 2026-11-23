using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Shows the host address of a picked device and stores it. It is written without Razor, because the test project
/// has none.
/// </summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(DiscoverableTestSubject))]
public class DiscoverableTestEditComponent : ComponentBase, ISubjectEditComponent, ISubjectDiscoveryComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }

    private string? HostAddress =>
        (DiscoveryResult as DiscoverableTestResult)?.HostAddress ?? (Subject as DiscoverableTestSubject)?.HostAddress;

    public bool IsValid => !string.IsNullOrEmpty(HostAddress);

    public bool IsDirty => IsValid;

    // The validity is fixed by the parameters, so neither event is ever raised.
    public event Action<bool>? IsValidChanged { add { } remove { } }

    public event Action<bool>? IsDirtyChanged { add { } remove { } }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-testid", "discoverable-host");
        builder.AddContent(2, $"Host: {HostAddress}");
        builder.CloseElement();
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Subject is DiscoverableTestSubject subject && HostAddress is { } hostAddress)
        {
            subject.HostAddress = hostAddress;
        }

        return Task.CompletedTask;
    }
}
