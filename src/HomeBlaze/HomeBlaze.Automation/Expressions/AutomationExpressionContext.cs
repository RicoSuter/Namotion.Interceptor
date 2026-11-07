using System.Collections.Immutable;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// Resolves the paths and variables of an automation expression for one <see cref="AutomationEvaluation"/>. A value that
/// is missing adds its path to the evaluation. Pipes and text are those of pages.
/// </summary>
internal readonly struct AutomationExpressionContext(AutomationEvaluation evaluation) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path)
    {
        if (AutomationPaths.ResolveProperty(evaluation.Resolver, path, evaluation.Rule) is not { } property)
        {
            evaluation.AddMissingPath(path);
            return ExpressionValue.Missing;
        }

        var value = evaluation.Values is { } values ? values.GetValue(property) : property.GetValue();
        if (value is null)
        {
            evaluation.AddMissingPath(path);
        }

        return ExpressionValue.FromProperty(property, value);
    }

    public ExpressionValue ResolveVariable(string variable, string? memberPath)
    {
        var value = variable switch
        {
            AutomationVariables.Now when memberPath is null => ExpressionValue.FromValue(evaluation.Now),
            AutomationVariables.Time when memberPath is null => ExpressionValue.FromValue(evaluation.TimeOfDay),
            AutomationVariables.Event => ResolveEvent(memberPath),
            AutomationVariables.Subject => ResolveSource(memberPath),
            _ => ExpressionValue.Missing
        };

        if (value.IsMissing)
        {
            evaluation.AddMissingPath(memberPath is null ? variable : variable + AutomationPaths.Separator + memberPath);
        }

        return value;
    }

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionDisplay.ApplyPipe(name, arguments, input, evaluation.Resolver, evaluation.Rule, timeZone: null);

    public string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone: null);

    private ExpressionValue ResolveEvent(string? memberPath)
    {
        if (evaluation.Event is not { } captured)
        {
            return ExpressionValue.Missing;
        }

        return memberPath is null
            ? ExpressionValue.FromValue(captured.Payload)
            : EventPayloadReader.Read(captured.Payload, memberPath, evaluation.Resolver);
    }

    private ExpressionValue ResolveSource(string? memberPath)
    {
        if (evaluation.Event is not { } captured)
        {
            return ExpressionValue.Missing;
        }

        return memberPath is null
            ? ExpressionValue.FromValue(captured.Source)
            : AutomationPaths.ReadFrom(evaluation.Resolver, memberPath, captured.Source);
    }
}
