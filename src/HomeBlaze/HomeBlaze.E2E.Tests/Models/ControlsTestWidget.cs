using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// The widget of <see cref="ControlsTestSubject"/>. It is written without Razor, because the test project has none.
/// </summary>
[SubjectComponent(SubjectComponentType.Widget, typeof(ControlsTestSubject))]
public class ControlsTestWidget : ComponentBase, ISubjectComponent
{
    private int _clicks;

    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private ControlsTestSubject? Controls => Subject as ControlsTestSubject;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var controls = Controls;
        if (controls is null)
        {
            return;
        }

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "style", "padding: 8px");

        builder.AddContent(2, CreateText("slider-value", $"Level: {controls.Level}"));

        builder.OpenComponent<MudSlider<int>>(4);
        builder.AddAttribute(5, nameof(MudSlider<int>.Min), 0);
        builder.AddAttribute(6, nameof(MudSlider<int>.Max), 100);
        builder.AddAttribute(7, nameof(MudSlider<int>.Value), controls.Level);
        builder.AddAttribute(8, nameof(MudSlider<int>.ValueChanged), EventCallback.Factory.Create<int>(this, value => controls.Level = value));
        builder.CloseComponent();

        builder.AddContent(9, CreateText("text-value", $"Text: {controls.Text}"));

        builder.OpenComponent<MudTextField<string>>(12);
        builder.AddAttribute(13, nameof(MudTextField<string>.Immediate), true);
        builder.AddAttribute(14, nameof(MudTextField<string>.Value), controls.Text);
        builder.AddAttribute(15, nameof(MudTextField<string>.ValueChanged), EventCallback.Factory.Create<string>(this, value => controls.Text = value));
        builder.CloseComponent();

        builder.OpenComponent<MudButton>(16);
        builder.AddAttribute(17, nameof(MudButton.OnClick), EventCallback.Factory.Create<MouseEventArgs>(this, () => _clicks++));
        builder.AddAttribute(18, nameof(MudButton.ChildContent), (RenderFragment)(content => content.AddContent(19, "Count")));
        builder.CloseComponent();

        builder.AddContent(20, CreateText("click-count", $"Clicks: {_clicks}"));

        builder.OpenElement(28, "div");
        builder.AddAttribute(29, "data-testid", "scroll-area");
        builder.AddAttribute(30, "style", "height: 60px; overflow: auto");
        builder.OpenElement(31, "div");
        builder.AddAttribute(32, "style", "height: 1000px");
        builder.AddContent(33, "A list that is longer than its area");
        builder.CloseElement();
        builder.CloseElement();

        builder.CloseElement();
    }

    private static RenderFragment CreateText(string testId, string text) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-testid", testId);
        builder.AddContent(2, text);
        builder.CloseElement();
    };
}
