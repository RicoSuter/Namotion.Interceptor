namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// An input of an operation action: a parameter, or a member of a record parameter with the key <c>parameter.Member</c>,
/// as <see cref="InvokeOperationAction.Arguments"/> stores it.
/// </summary>
/// <param name="Key">The key of the argument.</param>
/// <param name="Label">The name shown on the field.</param>
/// <param name="Type">The type of the parameter or member.</param>
/// <param name="IsRequired">Whether the operation needs a value.</param>
/// <param name="IsTemplate">Whether the text is a template, which it is for a string.</param>
/// <param name="Choices">The names of an enum or the values of a boolean, or null.</param>
public sealed record ArgumentField(string Key, string Label, Type Type, bool IsRequired, bool IsTemplate, IReadOnlyList<string>? Choices);
