using System.Collections.Immutable;

namespace HomeBlaze.Services.Expressions;

/// <summary>
/// What the host of an expression supplies for one evaluation. Implemented by a struct, which the evaluator takes by
/// reference, so that an evaluation allocates no context.
/// </summary>
public interface IExpressionContext
{
    /// <summary>
    /// Resolves a path as written. Its first segment can be a variable of <see cref="ExpressionVariables"/>. A path that
    /// leads nowhere, or a malformed one, gives <see cref="ExpressionValue.Missing"/> and does not throw.
    /// </summary>
    ExpressionValue ResolvePath(string path);

    /// <summary>Applies a pipe that the host accepted when it checked the expression.</summary>
    ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input);
}
