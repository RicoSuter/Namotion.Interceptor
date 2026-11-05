using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components;

/// <summary>
/// Evaluates an expression that has a bound value, in the case of a value map or in the text of a widget: <c>value</c> and
/// <c>subject</c> are those of the binding, any other path and the name of a map resolve from the scope, the case or the
/// widget, and so from the page that holds it. A property that the viewer may not read resolves to missing, and so does
/// <c>subject</c> itself when the viewer may not see it. Created on the stack for one evaluation.
/// </summary>
internal readonly struct ValueBindingContext(
    SubjectPathResolver resolver,
    IInterceptorSubject scope,
    ValueBinding binding,
    ITimeZoneDisplay? timeZone,
    IUserPermissions permissions) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path)
    {
        try
        {
            var property = resolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, scope);
            return property is null || !permissions.CanRead(property)
                ? ExpressionValue.Missing
                : ExpressionValue.FromProperty(property, property.GetValue());
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
            return permissions.CanShow(subject) ? ExpressionValue.FromValue(subject) : ExpressionValue.Missing;
        }

        try
        {
            var property = resolver.ResolveDisplayablePropertyFrom(memberPath, PathStyle.Canonical, subject);
            return property is null || !permissions.CanRead(property)
                ? ExpressionValue.Missing
                : ExpressionValue.FromProperty(property, property.GetValue());
        }
        catch
        {
            return ExpressionValue.Missing;
        }
    }

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionDisplay.ApplyPipe(name, arguments, input, resolver, scope, timeZone, permissions);

    public string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone);
}
