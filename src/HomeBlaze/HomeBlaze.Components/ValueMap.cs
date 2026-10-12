using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Says what text, colour and icon a value gets in the value widgets that refer to this map. Shows nothing itself.
/// </summary>
[Category("Dashboard")]
[Description("Text, colour and icon per value, for value widgets")]
[DisplayName("Value map")]
[SubjectIcon("Rule")]
[PageLayout]
[InterceptorSubject]
public partial class ValueMap : IConfigurable, ITitleProvider
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

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
