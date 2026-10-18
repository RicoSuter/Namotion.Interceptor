using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Breakpoint = MudBlazor.Breakpoint;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows its content only when the browser window is between two breakpoints wide. The content is always rendered;
/// only CSS hides it.
/// </summary>
[Category("Dashboard")]
[Description("Shows its content only on windows of some widths")]
[DisplayName("Responsive")]
[SubjectIcon("Devices")]
[PageLayout]
[InterceptorSubject]
public partial class Responsive : IConfigurable, ITitleProvider
{
    private const string SingleBreakpoints = "Xs, Sm, Md, Lg, Xl, Xxl";

    private const int BreakpointCount = (int)Breakpoint.Xxl + 1;

    private static readonly string[] CssClassTable = CreateCssClassTable();

    public Responsive()
    {
        From = Breakpoint.Xs;
        To = Breakpoint.Xxl;
    }

    /// <summary>
    /// The smallest breakpoint that shows the content, one of Xs, Sm, Md, Lg, Xl and Xxl. The content shows from the
    /// lower edge of this breakpoint.
    /// </summary>
    [Configuration]
    public partial Breakpoint From { get; set; }

    /// <summary>
    /// The largest breakpoint that shows the content, one of Xs, Sm, Md, Lg, Xl and Xxl, not before <see cref="From"/>.
    /// The content shows up to the upper edge of this breakpoint.
    /// </summary>
    [Configuration]
    public partial Breakpoint To { get; set; }

    /// <summary>
    /// The content.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public string? Title => null;

    /// <summary>
    /// Gets the classes of the element that holds the content: <c>responsive-widget</c> and the MudBlazor display classes
    /// that show it only from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The <see cref="From"/> argument.</param>
    /// <param name="to">The <see cref="To"/> argument.</param>
    /// <param name="cssClasses">The classes when both arguments are valid, otherwise null.</param>
    /// <param name="error">
    /// The widget argument error when an argument is no single breakpoint or <paramref name="from"/> is after
    /// <paramref name="to"/>, otherwise null.
    /// </param>
    public static bool TryGetCssClasses(Breakpoint from, Breakpoint to,
        [NotNullWhen(true)] out string? cssClasses, [NotNullWhen(false)] out string? error)
    {
        cssClasses = null;
        if (!IsSingleBreakpoint(from))
        {
            error = $"From must be one of {SingleBreakpoints}: {from}";
            return false;
        }

        if (!IsSingleBreakpoint(to))
        {
            error = $"To must be one of {SingleBreakpoints}: {to}";
            return false;
        }

        if (from > to)
        {
            error = $"From must not be after To: {from} to {to}";
            return false;
        }

        cssClasses = CssClassTable[(int)from * BreakpointCount + (int)to];
        error = null;
        return true;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static bool IsSingleBreakpoint(Breakpoint breakpoint) => breakpoint is >= Breakpoint.Xs and <= Breakpoint.Xxl;

    private static string[] CreateCssClassTable()
    {
        string[] names = ["xs", "sm", "md", "lg", "xl", "xxl"];
        var table = new string[BreakpointCount * BreakpointCount];
        for (var from = 0; from < BreakpointCount; from++)
        {
            for (var to = from; to < BreakpointCount; to++)
            {
                // A display class of a breakpoint applies from its lower edge, so the content hides from the lower edge of
                // the breakpoint after To. MudBlazor declares the classes of larger breakpoints later, so they win.
                var show = from == 0 ? "d-block" : $"d-none d-{names[from]}-block";
                var hide = to == BreakpointCount - 1 ? "" : $" d-{names[to + 1]}-none";
                table[from * BreakpointCount + to] = $"responsive-widget {show}{hide}";
            }
        }

        return table;
    }
}
