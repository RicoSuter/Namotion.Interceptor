using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// The root component of the test host: renders the routed page of the authentication assembly, or of a test page of this
/// assembly, without render mode.
/// </summary>
internal sealed class TestApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Router>(0);
        builder.AddComponentParameter(1, nameof(Router.AppAssembly), typeof(AuthenticationWebExtensions).Assembly);
        builder.AddComponentParameter(2, nameof(Router.AdditionalAssemblies), new[] { typeof(TestApp).Assembly });
        builder.AddComponentParameter(3, nameof(Router.Found), (RenderFragment<RouteData>)(routeData => routeBuilder =>
        {
            routeBuilder.OpenComponent<RouteView>(0);
            routeBuilder.AddComponentParameter(1, nameof(RouteView.RouteData), routeData);
            routeBuilder.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
