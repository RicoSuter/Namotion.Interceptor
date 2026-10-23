using System.Collections.Immutable;

namespace HomeBlaze.Services.Expressions;

/// <summary>A pipe after an expression, such as <c>| map:wallbox</c>: its name and its literal arguments as written.</summary>
public readonly record struct ExpressionPipe(string Name, ImmutableArray<string> Arguments);
