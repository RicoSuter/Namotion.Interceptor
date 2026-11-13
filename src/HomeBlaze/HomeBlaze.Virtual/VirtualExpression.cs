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
    public static ExpressionValue Evaluate(ParsedExpression expression, SubjectPathResolver resolver, IInterceptorSubject scope) =>
        Evaluate(expression, resolver, scope, out _);

    /// <summary>Evaluates an expression and gives the paths that led to a subject without the property they name.</summary>
    internal static ExpressionValue Evaluate(ParsedExpression expression, SubjectPathResolver resolver, IInterceptorSubject scope, out List<VirtualMiss>? misses)
    {
        var context = new VirtualExpressionContext(resolver, scope);
        var value = expression.EvaluateWithoutPipes(ref context);
        misses = context.Misses;
        return value;
    }

    /// <summary>
    /// Resolves the readable property a path leads to: a property of <paramref name="scope"/> for a name without
    /// <c>/</c>, otherwise relative to it, upwards with <c>../</c>, or from the root with a leading <c>/</c>. A secret or
    /// a property excluded from expressions is not readable. Null for a path that leads nowhere or is malformed.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, string path, IInterceptorSubject scope)
    {
        List<VirtualMiss>? misses = null;
        return ResolveProperty(resolver, path, scope, ref misses);
    }

    /// <summary>Resolves like the public overload and adds a path that led to a subject without the property to <paramref name="misses"/>.</summary>
    internal static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, string path, IInterceptorSubject scope, ref List<VirtualMiss>? misses)
    {
        var separator = path.LastIndexOf(SubjectPathResolver.Separator);
        var name = separator < 0 ? path : path[(separator + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        try
        {
            if (ResolveSubject(resolver, path, separator, scope) is not { } subject)
            {
                return null;
            }

            if (subject.TryGetRegisteredProperty(name) is not { } property)
            {
                (misses ??= []).Add(new VirtualMiss(subject, name));
                return null;
            }

            return property.IsDisplayable() ? property : null;
        }
        catch (Exception)
        {
            // A path is written by the user, and a malformed one must not fail the getter that reads it.
            return null;
        }
    }

    // An absolute path is walked relative to the root: the resolver caches absolute paths and reads nothing that derived
    // tracking records, while a relative walk reads each property that holds subjects on the way, so a subject that is
    // added or replaced there recalculates the property that resolved the path.
    // The subject part keeps its trailing separator, so that "./" and "../" reach the resolver as the prefixes it knows.
    private static IInterceptorSubject? ResolveSubject(SubjectPathResolver resolver, string path, int separator, IInterceptorSubject scope)
    {
        if (separator < 0)
        {
            return scope;
        }

        if (path[0] != SubjectPathResolver.Separator)
        {
            return resolver.ResolveSubject(path[..(separator + 1)], PathStyle.Canonical, scope);
        }

        var root = resolver.ResolveSubject("/", PathStyle.Canonical, scope);
        return root is null || separator == 0 ? root : resolver.ResolveSubject(path[1..(separator + 1)], PathStyle.Canonical, root);
    }
}
