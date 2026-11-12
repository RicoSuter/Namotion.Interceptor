using HomeBlaze.Authentication.Blazor.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A page that shows the <see cref="AuthenticationBanners"/> to everybody, as the layout of the application does.</summary>
[Route(Path)]
[AllowAnonymous]
public sealed class BannersTestPage : ComponentBase
{
    public const string Path = "/test/banners";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<AuthenticationBanners>(0);
        builder.CloseComponent();
    }
}
