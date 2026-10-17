using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Operations;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Checks the operations of a switch widget.
/// </summary>
internal static class SwitchArguments
{
    /// <summary>
    /// Gets the widget argument error of the operations of a switch, or null when they can be used. A path that
    /// leads to no operation gives no error: the switch is disabled until the operation exists.
    /// </summary>
    /// <param name="on">The path of the operation that turns the switch on, as written.</param>
    /// <param name="off">The path of the operation that turns the switch off, as written.</param>
    /// <param name="onMethod">The operation that <paramref name="on"/> leads to, or null.</param>
    /// <param name="offMethod">The operation that <paramref name="off"/> leads to, or null.</param>
    public static string? Validate(string? on, string? off, MethodMetadata? onMethod, MethodMetadata? offMethod)
    {
        if (string.IsNullOrEmpty(on) != string.IsNullOrEmpty(off))
        {
            return "Set both On and Off, or neither";
        }

        if (onMethod is not null && OperationInvoker.GetPrompt(onMethod) == OperationPrompt.Parameters)
        {
            return $"On needs an operation without input: {on}";
        }

        if (offMethod is not null && OperationInvoker.GetPrompt(offMethod) == OperationPrompt.Parameters)
        {
            return $"Off needs an operation without input: {off}";
        }

        return null;
    }
}
