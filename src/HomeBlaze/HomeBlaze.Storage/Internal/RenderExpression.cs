using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Expressions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Shows the value of an expression in the text of a page. A path with a / prefix resolves from the root. Every other path
/// resolves from the page, and its first segment can be the name of a block on that page.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
public partial class RenderExpression : ITitleProvider
{
    private readonly SubjectPathResolver _pathResolver;
    private readonly ParsedExpression? _expression;

    // The pipes up to the last map, the only pipe that reads properties.
    private readonly int _sharedPipeCount;

    // Null until the first evaluation; replaced as a whole by each one.
    private RegisteredSubjectProperty[]? _readProperties;

    /// <summary>The text between the braces, as the page has it.</summary>
    public string Text { get; }

    /// <summary>What is wrong with the expression as written, or null. It is shown in place of the value.</summary>
    public string? Error { get; }

    public MarkdownFile Parent { get; }

    public string? Title => null;

    // No production code reads this: a render reads the text through GetText, which also reads the version of the paths,
    // so it renders again when the target of an absolute path appears. Its change notification tells other observers when
    // the value changes, but not when a target appears. It is the value before the pipes format it.
    [Derived]
    public object? Value => EvaluateShared();

    /// <summary>
    /// The properties that the last evaluation of <see cref="Value"/> read, also those that the cases of a value map read,
    /// in the order they were first read. Each of them is a dependency of <see cref="Value"/>.
    /// </summary>
    public ImmutableArray<RegisteredSubjectProperty> ReadProperties
    {
        get
        {
            var readProperties = Volatile.Read(ref _readProperties);
            if (readProperties is null)
            {
                // Not evaluated yet, such as in a context that does not track derived properties.
                EvaluateShared();
                readProperties = Volatile.Read(ref _readProperties)!;
            }

            return ImmutableCollectionsMarshal.AsImmutableArray(readProperties);
        }
    }

    public RenderExpression(string text, MarkdownFile parent, SubjectPathResolver pathResolver)
    {
        Text = text;
        Parent = parent;
        _pathResolver = pathResolver;
        _expression = MarkdownExpressions.Parse(text, out var error);
        Error = error;
        _sharedPipeCount = _expression is null ? 0 : GetSharedPipeCount(_expression);
    }

    /// <summary>
    /// Gets the value as the page shows it to a viewer. A property that the viewer may not read is missing like one that
    /// does not exist, also in the cases of a value map, so that a hidden value cannot be told from a wrong path. A missing
    /// value gives an empty text.
    /// </summary>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for the time zone of the server.</param>
    /// <param name="permissions">The permissions of the viewer the text is shown to.</param>
    public string GetText(ITimeZoneDisplay? timeZone, IUserPermissions permissions)
    {
        if (_expression is null)
        {
            return string.Empty;
        }

        var context = new MarkdownExpressionContext(_pathResolver, Parent, timeZone, permissions);
        return context.ToText(_expression.Evaluate(ref context));
    }

    private static int GetSharedPipeCount(ParsedExpression expression)
    {
        for (var index = expression.Pipes.Length - 1; index >= 0; index--)
        {
            if (expression.Pipes[index].Name.Equals(ExpressionDisplay.MapPipe, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return 0;
    }

    // Evaluated without a check of any viewer, as the derived value and its dependencies are shared by every viewer. The
    // pipes up to the last map are applied as well, so that what the cases of a map read is recorded and is a dependency,
    // but only the value before them is kept.
    private object? EvaluateShared()
    {
        if (_expression is null)
        {
            Volatile.Write(ref _readProperties, []);
            return null;
        }

        var recorder = new ReadRecordingPermissions();
        var context = new MarkdownExpressionContext(_pathResolver, Parent, timeZone: null, recorder);
        var value = _expression.EvaluateWithoutPipes(ref context);
        var piped = value;
        for (var index = 0; index < _sharedPipeCount; index++)
        {
            var pipe = _expression.Pipes[index];
            piped = context.ApplyPipe(pipe.Name, pipe.Arguments, piped);
        }

        Volatile.Write(ref _readProperties, recorder.ToArray(Volatile.Read(ref _readProperties)));
        return value.Value;
    }
}
