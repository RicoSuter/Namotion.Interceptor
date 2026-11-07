using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Evaluates an expression in the text of a page: a path resolves from the page like the path of a widget, the pipes
/// are those of <see cref="ExpressionDisplay"/>, and <c>map</c> applies a value map that the page names. Created on the stack for one evaluation.
/// </summary>
/// <param name="pathResolver">The resolver of the paths and of the names of maps.</param>
/// <param name="page">The page the paths resolve from.</param>
/// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
/// <param name="checksReadAccess">Whether a property that <see cref="PropertyReadAccess"/> refuses resolves to missing, and
/// a value is read with <see cref="PropertyReadAccess.GetValue"/>.
/// False only for a value that is shared by every caller, which must not depend on the flow that computes it.</param>
internal readonly struct MarkdownExpressionContext(
    SubjectPathResolver pathResolver,
    IInterceptorSubject page,
    ITimeZoneDisplay? timeZone,
    bool checksReadAccess = true) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path)
    {
        RegisteredSubjectProperty? property;
        try
        {
            property = pathResolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, page);
        }
        catch
        {
            // The path is written by the author of the page, and a malformed one must not take the page down.
            return ExpressionValue.Missing;
        }

        if (property is null)
        {
            return ExpressionValue.Missing;
        }

        if (!checksReadAccess)
        {
            return ExpressionValue.FromProperty(property, property.GetValue());
        }

        return PropertyReadAccess.CanRead(property)
            ? ExpressionValue.FromProperty(property, PropertyReadAccess.GetValue(property))
            : ExpressionValue.Missing;
    }

    // The text of a page binds no variable, and MarkdownExpressions refuses one before an expression is evaluated.
    public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionDisplay.ApplyPipe(name, arguments, input, pathResolver, page, timeZone);

    public string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone);
}
