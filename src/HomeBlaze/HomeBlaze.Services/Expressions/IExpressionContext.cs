using System.Collections.Immutable;

namespace HomeBlaze.Services.Expressions;

/// <summary>
/// What the host of an expression supplies for one evaluation. Implemented by a struct, which the evaluator takes by
/// reference, so that an evaluation allocates no context.
/// </summary>
public interface IExpressionContext
{
    /// <summary>
    /// Resolves a path that does not start with a variable. A path that leads nowhere, or a malformed one, gives
    /// <see cref="ExpressionValue.Missing"/> and does not throw.
    /// </summary>
    ExpressionValue ResolvePath(string path);

    /// <summary>
    /// Resolves a path that starts with a variable of <see cref="ExpressionVariables"/>. A variable the host does not bind,
    /// or a path that leads nowhere, gives <see cref="ExpressionValue.Missing"/> and does not throw.
    /// </summary>
    /// <param name="variable"><see cref="ExpressionVariables.Value"/> or <see cref="ExpressionVariables.Subject"/>.</param>
    /// <param name="memberPath">The rest of the path after the variable and its slash, such as <c>IsCharging</c> for
    /// <c>subject/IsCharging</c>, or null for the variable alone.</param>
    ExpressionValue ResolveVariable(string variable, string? memberPath);

    /// <summary>Applies a pipe that the host accepted when it checked the expression.</summary>
    ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input);

    /// <summary>Gets the text that shows a result, as a template writes it into its text.</summary>
    string ToText(ExpressionValue result);
}
