using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Says what text, colour and icon a value gets in the value widgets that refer to this map, and in the pipe <c>map</c> of the text of a page. Shows nothing itself.
/// </summary>
[Category("Dashboard")]
[Description("Text, colour and icon per value, for value widgets")]
[DisplayName("Value map")]
[SubjectIcon("Rule")]
[PageLayout]
[InterceptorSubject]
public partial class ValueMap : IConfigurable, ITitleProvider, IValueMap
{
    public ValueMap()
    {
        Cases = [];
    }

    /// <summary>
    /// The cases, one per <c>Case</c> block.
    /// </summary>
    [Slot("Case")]
    public partial ValueMapCase[] Cases { get; set; }

    public string? Title => null;

    /// <summary>
    /// Returns the first case whose value equals the text without regard to case, or null.
    /// </summary>
    public ValueMapCase? FindCase(string rawText)
    {
        foreach (var valueCase in Cases)
        {
            if (string.Equals(valueCase.Value, rawText, StringComparison.OrdinalIgnoreCase))
            {
                return valueCase;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first case that matches the bound value, see <see cref="ValueMapCase.Matches"/>, or null.
    /// </summary>
    /// <param name="binding">The value and its subject.</param>
    /// <param name="resolver">The resolver of the paths of the conditions.</param>
    public ValueMapCase? FindCase(in ValueBinding binding, SubjectPathResolver resolver)
    {
        // Formatted once for all cases; the cases with a value compare it.
        var rawText = binding.Value is null ? null : RawValueFormatter.Format(binding.Value);
        foreach (var valueCase in Cases)
        {
            if (valueCase.Matches(rawText, binding, resolver))
            {
                return valueCase;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string? GetText(in ValueBinding binding, SubjectPathResolver resolver, ITimeZoneDisplay? timeZone) =>
        FindCase(binding, resolver)?.GetText(binding, resolver, timeZone);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
