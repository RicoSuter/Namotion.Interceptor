using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Finds the error boxes of a page for the tests that load whole pages: the pattern of a box and the
/// walk through everything a page shows are here once.
/// </summary>
internal static partial class PageErrorBoxes
{
    /// <summary>The class of the element of an error box. Nothing else that a page renders has it.</summary>
    public const string CssClass = "markdown-block-error";

    [GeneratedRegex($"""<div class="{CssClass}" role="alert">Line (\d+): (.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex BoxRegex();

    /// <summary>Gets the error boxes of a loaded page as the page shows them, <c>Line N: message</c>, ordered by their line.</summary>
    public static List<string> FindSortedByLine(MarkdownFile page) =>
        Find(page)
            .OrderBy(box => box.Line)
            .Select(box => $"Line {box.Line}: {box.Message}")
            .ToList();

    /// <summary>Gets the messages of the error boxes of a loaded page without their line numbers, in the order they are found.</summary>
    /// <remarks>
    /// The children of a page and of a fragment are found in the order of the page text, because the parser
    /// fills them in that order into a new dictionary and removes nothing from it. Inside a widget the boxes
    /// are found slot by slot, which is the order of the slot properties and not that of the page.
    /// </remarks>
    public static List<string> FindMessages(MarkdownFile page) =>
        Find(page)
            .Select(box => box.Message)
            .ToList();

    /// <summary>
    /// Renders the text of a page without creating a subject, with the content of every block in its place.
    /// It has the error boxes that are visible from the syntax alone.
    /// </summary>
    public static string RenderSyntaxOnly(MarkdownPage page) => Concatenate(MarkdownSegmentBuilder.Build(page));

    /// <summary>
    /// Gets the subjects of a page at every depth: the given ones, what is inside their fragments and the
    /// values of their slots.
    /// </summary>
    public static IEnumerable<IInterceptorSubject> Flatten(IEnumerable<IInterceptorSubject?> subjects)
    {
        foreach (var subject in subjects.OfType<IInterceptorSubject>())
        {
            yield return subject;

            var nested = subject is MarkdownFragment fragment ? fragment.Children.Values : GetSlotValues(subject);
            foreach (var child in Flatten(nested))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<(int Line, string Message)> Find(MarkdownFile page) =>
        Flatten(page.Children.Values)
            .OfType<HtmlSegment>()
            .SelectMany(segment => BoxRegex().Matches(segment.Html))
            .Select(match => (int.Parse(match.Groups[1].Value), WebUtility.HtmlDecode(match.Groups[2].Value)));

    private static IEnumerable<IInterceptorSubject?> GetSlotValues(IInterceptorSubject subject) =>
        subject.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<SlotAttribute>(true) is not null)
            .SelectMany(property => property.GetValue(subject) switch
            {
                IInterceptorSubject value => [value],
                IEnumerable<IInterceptorSubject?> values => values,
                _ => []
            });

    private static string Concatenate(IEnumerable<MarkdownSegment> segments) =>
        string.Concat(segments.Select(segment => segment switch
        {
            HtmlMarkdownSegment html => html.Html,
            BlockMarkdownSegment block => Concatenate(block.Content),
            _ => string.Empty
        }));
}
