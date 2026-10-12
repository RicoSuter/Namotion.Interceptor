using System.Text;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The whole answer of a turn as streamed. Text after <see cref="StartSegment"/> is separated from the text before it
/// by a blank line that is streamed too, so the live text and the stored answer read the same.
/// </summary>
internal sealed class AgentAnswerText
{
    private const string SegmentSeparator = "\n\n";

    private StringBuilder? _text;
    private bool _isNewSegment;

    /// <summary>The answer so far, or null when no text was streamed.</summary>
    public string? Text => _text?.ToString();

    /// <summary>Marks that the next text starts a new segment, for example after a tool call.</summary>
    public void StartSegment() => _isNewSegment = true;

    /// <summary>Appends streamed text and reports it, preceded by the separator when it starts a new segment.</summary>
    public void Append(string text, IProgress<AgentTurnUpdate> progress)
    {
        if (text.Length == 0)
        {
            return;
        }

        _text ??= new StringBuilder();
        if (_isNewSegment && _text.Length > 0)
        {
            _text.Append(SegmentSeparator);
            progress.Report(new AgentTextDelta(SegmentSeparator));
        }

        _isNewSegment = false;
        _text.Append(text);
        progress.Report(new AgentTextDelta(text));
    }
}
