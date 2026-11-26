using System.Collections.Immutable;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Virtual;

/// <summary>
/// Resolves the paths of a virtual property's expression from the subject the property is on.
/// </summary>
internal readonly struct VirtualExpressionContext(SubjectPathResolver resolver, IInterceptorSubject scope) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path) =>
        VirtualExpression.ResolveProperty(resolver, path, scope) is { } property
            ? ExpressionValue.FromProperty(property, property.GetValue())
            : ExpressionValue.Missing;

    public readonly ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

    // Parsing rejects pipes, so none reaches the context.
    public readonly ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

    public readonly string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone: null);
}
