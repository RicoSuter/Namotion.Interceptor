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

    /// <summary>
    /// Resolves like the public overload and adds to <paramref name="misses"/> the subject that a path led to without the
    /// property it names next, also a middle segment, when that property could be added at run time.
    /// </summary>
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
            if (GetStart(resolver, path, scope, out var walkStart) is not { } start)
            {
                return null;
            }

            var subject = walkStart > separator ? start : resolver.ResolveSubject(path[walkStart..(separator + 1)], PathStyle.Canonical, start);
            if (subject is null)
            {
                AddMissOfMissingSegment(resolver, path, start, walkStart, separator, ref misses);
                return null;
            }

            if (subject.TryGetRegisteredSubject() is not { } registered)
            {
                return null;
            }

            if (registered.TryGetProperty(name) is not { } property)
            {
                AddMiss(subject, name, ref misses);
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

    // Gets the subject the walk starts at and the index in the path where it starts. An absolute path is walked relative
    // to the root: the resolver caches absolute paths and reads nothing that derived tracking records, while a relative
    // walk reads each property that holds subjects on the way, so a subject that is added or replaced there recalculates
    // the property that resolved the path.
    private static IInterceptorSubject? GetStart(SubjectPathResolver resolver, string path, IInterceptorSubject scope, out int walkStart)
    {
        walkStart = 0;
        if (path[0] != SubjectPathResolver.Separator)
        {
            return scope;
        }

        walkStart = 1;
        return resolver.ResolveSubject("/", PathStyle.Canonical, scope);
    }

    // Only for a path that did not resolve: finds the last subject on the way and the segment after it, by resolving ever
    // shorter parts of the path, as a failed step reports too little to the resolver's recorder to tell where it failed.
    // A part keeps its trailing separator, so that "./" and "../" reach the resolver as the prefixes it knows.
    private static void AddMissOfMissingSegment(
        SubjectPathResolver resolver, string path, IInterceptorSubject start, int walkStart, int separator, ref List<VirtualMiss>? misses)
    {
        var segmentEnd = separator;
        while (segmentEnd > walkStart)
        {
            var boundary = path.LastIndexOf(SubjectPathResolver.Separator, segmentEnd - 1, segmentEnd - walkStart);
            var parent = boundary < 0 ? start : resolver.ResolveSubject(path[walkStart..(boundary + 1)], PathStyle.Canonical, start);
            if (parent is not null)
            {
                var segment = path[(boundary < 0 ? walkStart : boundary + 1)..segmentEnd];
                if (parent.TryGetRegisteredSubject() is { } registered && registered.TryGetProperty(segment) is null)
                {
                    AddMiss(parent, segment, ref misses);
                }

                return;
            }

            segmentEnd = boundary;
        }
    }

    // An empty name, a name with an index, or "." or "..", is never added as a property, so waiting for it would never end.
    private static void AddMiss(IInterceptorSubject subject, string name, ref List<VirtualMiss>? misses)
    {
        if (name is not ("" or "." or "..") && !name.Contains('['))
        {
            (misses ??= []).Add(new VirtualMiss(subject, name));
        }
    }
}
