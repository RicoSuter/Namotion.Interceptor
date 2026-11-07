using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>An input of an action that has no valid value: the key of its field, its label and what is wrong.</summary>
internal sealed record ActionInputError(string Key, string Label, string Error);

/// <summary>
/// Checks the arguments of operation actions and the values of property writes against the operation or property they
/// target. The editor shows the result on the fields and the draft counts it as an error, so that it does not depend on
/// which part of the editor is displayed.
/// </summary>
internal static class ActionInputs
{
    /// <summary>The key of the error of the value of a property write.</summary>
    public const string ValueKey = "value";

    /// <summary>The fields of the operation that the target is, or null when the target is no operation.</summary>
    public static IReadOnlyList<ArgumentField>? GetFields(RegisteredSubjectProperty? target) =>
        target?.TryGetMethodMetadata() is { } method ? ArgumentFields.Create(method) : null;

    /// <summary>The operation at the path, or null when the path leads to none.</summary>
    public static MethodMetadata? GetMethod(Automation automation, string ruleKey, string path) =>
        AutomationPreview.ResolveTarget(automation, ruleKey, path)?.TryGetMethodMetadata();

    /// <summary>The path of the operation or property the action targets, or null for an action without one.</summary>
    public static string? GetTargetPath(AutomationAction action) => action switch
    {
        InvokeOperationAction invoke => invoke.OperationPath,
        SetPropertyAction write => write.PropertyPath,
        _ => null
    };

    public static IReadOnlyList<ActionInputError> Check(AutomationAction action, Automation automation, string ruleKey, RuleExpressions expressions)
    {
        var target = AutomationPreview.ResolveTarget(automation, ruleKey, GetTargetPath(action));
        return Check(action, action is InvokeOperationAction ? GetFields(target) : null, action is SetPropertyAction ? target?.Type : null, expressions);
    }

    /// <summary>
    /// The errors of an action whose operation has the fields, or whose property has the type. A path that leads nowhere is
    /// reported by the automation itself, so an operation without fields has no input errors here.
    /// </summary>
    public static IReadOnlyList<ActionInputError> Check(
        AutomationAction action, IReadOnlyList<ArgumentField>? fields, Type? targetType, RuleExpressions expressions)
    {
        if (action is InvokeOperationAction invoke && fields is not null)
        {
            return
            [
                .. fields
                    .Select(field => (Field: field, Error: ArgumentFields.Validate(GetArgument(invoke, field.Key), field.Type, field.IsRequired, expressions)))
                    .Where(entry => entry.Error is not null)
                    .Select(entry => new ActionInputError(entry.Field.Key, entry.Field.Label, entry.Error!))
            ];
        }

        if (action is SetPropertyAction write)
        {
            var isRequired = !ArgumentFields.IsText(targetType) && !string.IsNullOrWhiteSpace(write.PropertyPath);
            return ArgumentFields.Validate(write.Value, targetType, isRequired, expressions) is { } error ? [new ActionInputError(ValueKey, "Value", error)] : [];
        }

        return [];
    }

    /// <summary>The text of the argument with the key, which the binder matches without regard to case.</summary>
    public static string? GetArgument(InvokeOperationAction invoke, string key) =>
        invoke.Arguments.FirstOrDefault(argument => string.Equals(argument.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The name of an action in a message, as the automation names it: by its id, or by its position.</summary>
    public static string Name(AutomationAction action, int index) => action.Id.Length > 0 ? $"action '{action.Id}'" : $"action {index + 1}";
}
