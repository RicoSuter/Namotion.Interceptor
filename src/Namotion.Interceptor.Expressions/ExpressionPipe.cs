using System.Collections.Immutable;

namespace Namotion.Interceptor.Expressions;

/// <summary>A pipe after an expression, such as <c>| format:N1</c>: its name and its literal arguments as written.</summary>
public readonly record struct ExpressionPipe(string Name, ImmutableArray<string> Arguments);
