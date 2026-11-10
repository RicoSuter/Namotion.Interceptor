using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Evaluates an expression in the text of a page: a path resolves from the page like the path of a widget, the pipes
/// are those of <see cref="ExpressionDisplay"/>, and <c>map</c> applies a value map that the page names. Created on the stack for one evaluation.
/// </summary>
/// <param name="pathResolver">The resolver of the paths and of the names of maps.</param>
/// <param name="page">The page the paths resolve from.</param>
/// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
/// <param name="permissions">The permissions of the viewer, for whom a property it may not read, or of a subject it is not
/// shown, resolves to missing, also in the cases of a map, and a readable one to its value as the viewer is shown it.</param>
internal readonly struct MarkdownExpressionContext(
    SubjectPathResolver pathResolver,
    IInterceptorSubject page,
    ITimeZoneDisplay? timeZone,
    IUserPermissions permissions) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path)
    {
        RegisteredSubjectProperty? property;
        try
        {
            property = pathResolver.ResolveShownProperty(path, page, permissions);
        }
        catch
        {
            // The path is written by the author of the page, and a malformed one must not take the page down.
            return ExpressionValue.Missing;
        }

        return property is null
            ? ExpressionValue.Missing
            : ExpressionValue.FromProperty(property, permissions.GetValue(property));
    }

    // The text of a page binds no variable, and MarkdownExpressions refuses one before an expression is evaluated.
    public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionDisplay.ApplyPipe(name, arguments, input, pathResolver, page, timeZone, permissions);

    public string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone);
}
