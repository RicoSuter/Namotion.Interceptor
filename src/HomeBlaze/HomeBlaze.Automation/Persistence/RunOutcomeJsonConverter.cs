using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeBlaze.Automation.Persistence;

/// <summary>
/// Writes a run outcome by name and reads a name that names no outcome, such as one an earlier version wrote, as no
/// outcome, so that such a checkpoint keeps its state and its run counts.
/// </summary>
internal sealed class RunOutcomeJsonConverter : JsonConverter<AutomationRunOutcome?>
{
    public override bool HandleNull => true;

    public override AutomationRunOutcome? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        var name = reader.GetString();
        return name is { Length: > 0 } && char.IsAsciiLetter(name[0]) && Enum.TryParse<AutomationRunOutcome>(name, ignoreCase: true, out var outcome)
            ? outcome
            : null;
    }

    public override void Write(Utf8JsonWriter writer, AutomationRunOutcome? value, JsonSerializerOptions options)
    {
        if (value is { } outcome)
        {
            writer.WriteStringValue(outcome.ToString());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
