using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Shows the rows of a <see cref="LiveGridSubject"/> only in templates that MudBlazor components render on their own: the
/// cells of a virtualized data grid, the rows of a table and the items of a menu, which a popover renders elsewhere.
/// </summary>
[SubjectComponent(SubjectComponentType.Widget, typeof(LiveGridSubject))]
public class LiveGridWidget : ComponentBase, ISubjectComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Subject is not LiveGridSubject grid)
        {
            return;
        }

        var rows = grid.Rows;

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-testid", "live-grid");
        builder.OpenComponent<MudDataGrid<LiveValuesSubject>>(2);
        builder.AddComponentParameter(3, nameof(MudDataGrid<LiveValuesSubject>.Items), rows);
        builder.AddComponentParameter(4, nameof(MudDataGrid<LiveValuesSubject>.Virtualize), true);
        builder.AddComponentParameter(5, nameof(MudDataGrid<LiveValuesSubject>.Height), "200px");
        builder.AddComponentParameter(6, nameof(MudDataGrid<LiveValuesSubject>.FixedHeader), true);
        builder.AddComponentParameter(7, nameof(MudDataGrid<LiveValuesSubject>.Columns), (RenderFragment)(columns =>
        {
            columns.OpenComponent<TemplateColumn<LiveValuesSubject>>(0);
            columns.AddComponentParameter(1, nameof(TemplateColumn<LiveValuesSubject>.Title), "Text");
            columns.AddComponentParameter(2, nameof(TemplateColumn<LiveValuesSubject>.CellTemplate),
                (RenderFragment<CellContext<LiveValuesSubject>>)(cell => content =>
                {
                    content.OpenElement(0, "span");
                    content.AddAttribute(1, "data-testid", "grid-cell");
                    content.AddContent(2, cell.Item.Text);
                    content.CloseElement();
                }));
            columns.CloseComponent();
        }));
        builder.CloseComponent();
        builder.CloseElement();

        builder.OpenElement(10, "div");
        builder.AddAttribute(11, "data-testid", "live-table");
        builder.OpenComponent<MudTable<LiveValuesSubject>>(12);
        builder.AddComponentParameter(13, nameof(MudTable<LiveValuesSubject>.Items), rows.Take(3).ToArray());
        builder.AddComponentParameter(14, nameof(MudTable<LiveValuesSubject>.RowTemplate),
            (RenderFragment<LiveValuesSubject>)(row => content =>
            {
                content.OpenComponent<MudTd>(0);
                content.AddComponentParameter(1, nameof(MudTd.ChildContent), (RenderFragment)(cell => cell.AddContent(0, row.Text)));
                content.CloseComponent();
            }));
        builder.CloseComponent();
        builder.CloseElement();

        builder.OpenComponent<MudMenu>(20);
        builder.AddComponentParameter(21, nameof(MudMenu.Label), "Live menu");
        builder.AddComponentParameter(22, nameof(MudMenu.ChildContent), (RenderFragment)(items =>
        {
            foreach (var row in rows.Take(2))
            {
                items.OpenComponent<MudMenuItem>(0);
                items.SetKey(row);
                items.AddComponentParameter(1, nameof(MudMenuItem.ChildContent), (RenderFragment)(item => item.AddContent(0, row.Text)));
                items.CloseComponent();
            }
        }));
        builder.CloseComponent();
    }
}
