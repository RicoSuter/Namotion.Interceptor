using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Binds configured texts to the parameters of an operation or to a property. The text of a string is a text template.
/// Any other text is a constant when it converts to the type, otherwise an expression whose value is converted; an
/// unknown expression fails. A record parameter binds its members from <c>parameter.Member</c> keys, as the operation
/// dialog does per member. Keys match without regard to case, and a key that names no parameter or member fails. Values
/// are the raw stored values, as in expressions and configuration files, so a constant is never scaled by the unit of
/// its parameter. Collects a display of the bound values.
/// </summary>
internal sealed class ArgumentBinder(AutomationEvaluation evaluation, bool allowsEvent)
{
    private readonly List<string> _display = [];

    /// <summary>The bound values as <c>name: value</c>, separated by commas, such as <c>speed: 300</c>.</summary>
    public string Display => string.Join(", ", _display);

    /// <summary>Binds the parameters that require input, in their order.</summary>
    public bool TryBindParameters(
        IReadOnlyList<MethodParameter> parameters,
        IReadOnlyDictionary<string, string> arguments,
        [NotNullWhen(true)] out object?[]? values,
        [NotNullWhen(false)] out string? error)
    {
        var inputs = parameters.Where(parameter => parameter.RequiresInput).ToArray();
        if (!TryValidateKeys(inputs, arguments, out error))
        {
            values = null;
            return false;
        }

        var bound = new object?[inputs.Length];
        for (var index = 0; index < inputs.Length; index++)
        {
            if (!TryBindParameter(inputs[index], arguments, out bound[index], out error))
            {
                values = null;
                return false;
            }
        }

        values = bound;
        error = null;
        return true;
    }

    /// <summary>Binds one text to a value of the type.</summary>
    public bool TryBindValue(string name, string text, Type type, out object? value, [NotNullWhen(false)] out string? error)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        if (targetType == typeof(string))
        {
            return TryRender(name, text, out value, out error);
        }

        if (ParameterConverter.TryConvert(text, type, out value))
        {
            _display.Add($"{name}: {text}");
            error = null;
            return true;
        }

        return TryEvaluate(name, text, type, out value, out error);
    }

    private static bool TryValidateKeys(
        MethodParameter[] inputs, IReadOnlyDictionary<string, string> arguments, [NotNullWhen(false)] out string? error)
    {
        foreach (var key in arguments.Keys)
        {
            if (!TryValidateKey(inputs, arguments, key, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryValidateKey(
        MethodParameter[] inputs, IReadOnlyDictionary<string, string> arguments, string key, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (inputs.Any(parameter => IsSame(parameter.Name, key)))
        {
            return true;
        }

        var separator = key.IndexOf('.');
        var owner = separator < 0 ? null : inputs.FirstOrDefault(parameter => IsSame(parameter.Name, key[..separator]));
        if (owner is null || !ParameterConverter.IsSupportedObject(owner.Type))
        {
            error = $"The argument '{key}' matches no parameter of the operation";
        }
        else if (!ParameterConverter.GetObjectMembers(owner.Type).Any(member => IsSame(member.Name, key[(separator + 1)..])))
        {
            error = $"The argument '{key}' matches no member of '{owner.Name}'";
        }
        else if (TryFind(arguments, owner.Name, out _))
        {
            error = $"The argument '{owner.Name}' is given both whole and per member";
        }

        return error is null;
    }

    private static bool IsSame(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private bool TryBindParameter(
        MethodParameter parameter, IReadOnlyDictionary<string, string> arguments, out object? value, [NotNullWhen(false)] out string? error)
    {
        if (TryFind(arguments, parameter.Name, out var text))
        {
            return TryBindValue(parameter.Name, text, parameter.Type, out value, out error);
        }

        if (ParameterConverter.IsSupportedObject(parameter.Type) && HasMembers(arguments, parameter.Name))
        {
            return TryBindObject(parameter, arguments, out value, out error);
        }

        value = null;
        if (parameter.IsNullable)
        {
            error = null;
            return true;
        }

        error = $"The argument '{parameter.Name}' is missing";
        return false;
    }

    private bool TryBindObject(
        MethodParameter parameter, IReadOnlyDictionary<string, string> arguments, out object? value, [NotNullWhen(false)] out string? error)
    {
        var inputs = new Dictionary<string, string?>();
        foreach (var member in ParameterConverter.GetObjectMembers(parameter.Type))
        {
            var key = parameter.Name + "." + member.Name;
            if (!TryFind(arguments, key, out var text))
            {
                continue;
            }

            if (!TryBindValue(key, text, member.Type, out var memberValue, out error))
            {
                value = null;
                return false;
            }

            inputs[member.Name] = RawValueFormatter.Format(memberValue);
        }

        if (ParameterConverter.TryCreateObject(parameter.Type, inputs, out value, out var createError))
        {
            error = null;
            return true;
        }

        error = $"The argument '{parameter.Name}' is invalid: {createError}";
        return false;
    }

    private bool TryRender(string name, string text, out object? value, [NotNullWhen(false)] out string? error)
    {
        if (!AutomationExpressionCheck.TryParseTemplate(text, allowsEvent, out var template, out var parseError))
        {
            value = null;
            error = $"The argument '{name}' is invalid: {parseError}";
            return false;
        }

        var rendered = evaluation.Render(template);
        _display.Add($"{name}: \"{rendered}\"");
        value = rendered;
        error = null;
        return true;
    }

    private bool TryEvaluate(string name, string text, Type type, out object? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        if (!AutomationExpressionCheck.TryParseExpression(text, allowsEvent, out var expression, out var parseError, "value"))
        {
            error = $"The argument '{name}' is invalid: {parseError}";
            return false;
        }

        if (evaluation.Evaluate(expression).Value is not { } result)
        {
            error = DescribeUnknown(name);
            return false;
        }

        if (!AutomationValueConverter.TryConvert(result, type, out value))
        {
            error = $"The argument '{name}' cannot be converted to {(Nullable.GetUnderlyingType(type) ?? type).Name}: {RawValueFormatter.Format(result)}";
            return false;
        }

        _display.Add($"{name}: {RawValueFormatter.Format(value)}");
        error = null;
        return true;
    }

    private string DescribeUnknown(string name) =>
        evaluation.MissingPaths.Count > 0
            ? $"The argument '{name}' is unknown, missing {string.Join(", ", evaluation.MissingPaths)}"
            : $"The argument '{name}' is unknown";

    private static bool TryFind(IReadOnlyDictionary<string, string> arguments, string key, [NotNullWhen(true)] out string? text)
    {
        foreach (var (name, value) in arguments)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                text = value;
                return true;
            }
        }

        text = null;
        return false;
    }

    private static bool HasMembers(IReadOnlyDictionary<string, string> arguments, string parameterName) =>
        arguments.Keys.Any(key => key.StartsWith(parameterName + ".", StringComparison.OrdinalIgnoreCase));
}
