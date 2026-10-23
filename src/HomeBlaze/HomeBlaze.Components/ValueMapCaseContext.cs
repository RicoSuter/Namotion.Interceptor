using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using HomeBlaze.Services.Expressions;
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Evaluates the condition or the text of a case of a value map: <c>value</c> and <c>subject</c> are those of the binding,
/// and any other path resolves from the page that holds the map. Created on the stack for one evaluation.
/// </summary>
internal readonly struct ValueMapCaseContext(
    SubjectPathResolver resolver,
    IInterceptorSubject valueCase,
    ValueBinding binding,
    ITimeZoneDisplay? timeZone) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path)
    {
        try
        {
            var property = resolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, valueCase);
            return property is null ? ExpressionValue.Missing : ExpressionValue.FromProperty(property, property.GetValue());
        }
        catch
        {
            // The path is written by the author of a page, and a malformed one must not take the page down.
            return ExpressionValue.Missing;
        }
    }

    public ExpressionValue ResolveVariable(string variable, string? memberPath)
    {
        if (variable == ExpressionVariables.Value)
        {
            // The value has no members of its own: a path into it is written from its subject.
            if (memberPath is not null || binding.Value is not { } value)
            {
                return ExpressionValue.Missing;
            }

            return binding.Property is { } property ? ExpressionValue.FromProperty(property, value) : ExpressionValue.FromValue(value);
        }

        if (binding.Subject is not { } subject)
        {
            return ExpressionValue.Missing;
        }

        if (memberPath is null)
        {
            return ExpressionValue.FromValue(subject);
        }

        try
        {
            var property = resolver.ResolveDisplayablePropertyFrom(memberPath, PathStyle.Canonical, subject);
            return property is null ? ExpressionValue.Missing : ExpressionValue.FromProperty(property, property.GetValue());
        }
        catch
        {
            return ExpressionValue.Missing;
        }
    }

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionDisplay.ApplyFormat(name, input, timeZone);

    public string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone);
}
