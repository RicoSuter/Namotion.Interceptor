using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A page with the antiforgery token of the visitor, as the dialogs of the app carry it in their forms.</summary>
[Route(Path)]
public sealed class AntiforgeryTestPage : ComponentBase
{
    public const string Path = "/test/antiforgery";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "form");
        builder.OpenComponent<AntiforgeryToken>(1);
        builder.CloseComponent();
        builder.CloseElement();
    }
}
