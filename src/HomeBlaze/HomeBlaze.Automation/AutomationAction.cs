using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Automation;

/// <summary>
/// One step of a rule. Serialized with a stable <c>$type</c> discriminator. An action that cannot be read, such as one
/// with an unknown discriminator or a key that names no setting, makes its rule invalid and is saved back as it was
/// written.
/// </summary>
[JsonConverter(typeof(AutomationActionJsonConverter))]
public abstract record AutomationAction
{
    /// <summary>A stable identifier of the action within its rule, used in run messages and logs.</summary>
    [Configuration]
    public string Id { get; init; } = string.Empty;

    /// <summary>What the rule does when this action fails.</summary>
    [Configuration]
    public ActionErrorPolicy OnError { get; init; }
}

/// <summary>Calls an operation of a subject and waits until it completes.</summary>
public sealed record InvokeOperationAction : AutomationAction
{
    /// <summary>The path of the operation, such as <c>/Living/Blinds/Close</c>.</summary>
    [Configuration]
    public string OperationPath { get; init; } = string.Empty;

    /// <summary>
    /// The arguments by parameter name, or by <c>parameter.Member</c> for the members of a record parameter. The text
    /// of a string parameter or member is a text template; any other text is a constant when it converts to the type
    /// of the parameter, otherwise an expression.
    /// </summary>
    [Configuration]
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();
}

/// <summary>Writes a writable property that is no configuration.</summary>
public sealed record SetPropertyAction : AutomationAction
{
    /// <summary>The path of the property.</summary>
    [Configuration]
    public string PropertyPath { get; init; } = string.Empty;

    /// <summary>The value, read like an argument of <see cref="InvokeOperationAction"/>.</summary>
    [Configuration]
    public string Value { get; init; } = string.Empty;
}

/// <summary>Changes the state of the automation that owns the rule.</summary>
public sealed record ChangeStateAction : AutomationAction
{
    /// <summary>The state to enter, one of the states of the automation.</summary>
    [Configuration]
    public string TargetState { get; init; } = string.Empty;
}

/// <summary>
/// An action that could not be read: the element as it was written and the reason. Its <see cref="AutomationAction.Id"/>
/// is the written <c>id</c> when that is a text, so that messages name it.
/// </summary>
internal sealed record UnreadableAction : AutomationAction
{
    public UnreadableAction(JsonElement json, string error)
    {
        Json = json;
        Error = error;
        Id = FindId(json);
    }

    public JsonElement Json { get; }

    public string Error { get; }

    // The naming of the configuration decides whether the key is "id" or "Id".
    private static string FindId(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (property.NameEquals("id") || property.NameEquals("Id"))
                {
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : string.Empty;
                }
            }
        }

        return string.Empty;
    }
}
