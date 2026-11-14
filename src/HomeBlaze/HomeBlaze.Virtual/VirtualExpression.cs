using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Virtual;

/// <summary>Parses, resolves and evaluates the expressions and paths of virtual properties.</summary>
public static class VirtualExpression
{
    /// <summary>
    /// Parses the expression of a virtual property: paths divided by <c>/</c>, no variables, no pipes.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out ParsedExpression? expression, [NotNullWhen(false)] out string? error)
    {
        if (!ParsedExpression.TryParse(text, SubjectPathResolver.Separator, ImmutableArray<string>.Empty, out expression, out error))
        {
            return false;
        }

        if (expression.Pipes.Length > 0)
        {
            expression = null;
            error = "Pipes are not supported in a virtual property.";
            return false;
        }

        return true;
    }

    /// <summary>Evaluates an expression with its paths resolved from <paramref name="scope"/>.</summary>
    public static ExpressionValue Evaluate(ParsedExpression expression, SubjectPathResolver resolver, IInterceptorSubject scope)
    {
        var context = new VirtualExpressionContext(resolver, scope);
        return expression.EvaluateWithoutPipes(ref context);
    }

    /// <summary>
    /// Resolves the readable property a path leads to: a property of <paramref name="scope"/> for a name without
    /// <c>/</c>, otherwise relative to it, upwards with <c>../</c>, or from the root with a leading <c>/</c>. A secret or
    /// a property excluded from expressions is not readable. Null for a path that leads nowhere or is malformed.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, string path, IInterceptorSubject scope)
    {
        var separator = path.LastIndexOf(SubjectPathResolver.Separator);
        var name = separator < 0 ? path : path[(separator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        try
        {
            var subject = separator < 0 ? scope : resolver.ResolveSubject(path[..(separator + 1)], PathStyle.Canonical, scope);
            return subject?.TryGetRegisteredSubject()?.TryGetProperty(name) is { } property && property.IsDisplayable() ? property : null;
        }
        catch (Exception)
        {
            // A path is written by the user, and a malformed one must not fail the getter that reads it.
            return null;
        }
    }
}
