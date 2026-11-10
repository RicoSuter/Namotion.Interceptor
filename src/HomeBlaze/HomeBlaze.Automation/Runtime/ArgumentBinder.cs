using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Binds configured texts to the parameters of an operation or to a property. The text of a string is a text template.
/// An empty text, also one of white space only, is null where null is accepted: for a nullable value type; for a parameter
/// of a reference type that is annotated as nullable or defaults to null, see <see cref="MethodParameter.IsNullable"/>;
/// for a property of a reference type that is not annotated as non-nullable, so also one without annotations, and for
/// every property of a reference type in an app that cannot read annotations, such as a trimmed one; and for a member of
/// a record parameter of any reference type. Any other text is a constant when it converts to the type, otherwise an expression whose value is converted as
/// <see cref="ParameterConverter.TryConvertValue"/> does; an unknown expression fails. Constants are read as expressions
/// write them: a duration as <see cref="AutomationDuration"/> reads it or as a <see cref="TimeSpan"/> with a <c>:</c>, so a
/// bare number is no duration; an instant in ISO 8601 with a date, such as <c>2026-11-06T10:00</c>, in UTC without an
/// offset, or as a time of day alone, such as <c>10:00</c>, in UTC on the UTC date of <c>now</c>; a number in the
/// invariant culture, also with an exponent, and finite; an enum by the name of a member, the number of a member, or the
/// names of members only of a flags enum. A duration longer than <see cref="AutomationDuration.Maximum"/> in either
/// direction fails, also as the value of an expression. A record parameter binds its members from
/// <c>parameter.Member</c> keys, as the operation dialog does per member. Keys match without regard to case, and a key
/// that names no parameter or member, or that is given twice, fails. Values are the raw stored values, as in
/// expressions and configuration files, so a constant is never scaled by the unit of its parameter. Collects a display
/// of the bound values.
/// </summary>
internal sealed class ArgumentBinder(AutomationEvaluation evaluation, bool allowsEvent)
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // The constants that expressions write differently from the operation dialog, which ParameterConverter.TryConvert reads.
    private static readonly FrozenDictionary<Type, ConstantParser> ConstantParsers = new Dictionary<Type, ConstantParser>
    {
        [typeof(int)] = static (string text, out object? value) =>
            Box(int.TryParse(text, NumberStyles.Integer, Culture, out var number), number, out value),
        [typeof(long)] = static (string text, out object? value) =>
            Box(long.TryParse(text, NumberStyles.Integer, Culture, out var number), number, out value),
        [typeof(decimal)] = static (string text, out object? value) =>
            Box(decimal.TryParse(text, NumberStyles.Float, Culture, out var number), number, out value),
        [typeof(double)] = static (string text, out object? value) =>
            Box(double.TryParse(text, NumberStyles.Float, Culture, out var number) && double.IsFinite(number), number, out value),
        [typeof(float)] = static (string text, out object? value) =>
            Box(float.TryParse(text, NumberStyles.Float, Culture, out var number) && float.IsFinite(number), number, out value),
        [typeof(TimeSpan)] = static (string text, out object? value) =>
            Box(AutomationDuration.TryParse(text, out var duration) || (text.Contains(':') && TimeSpan.TryParse(text, Culture, out duration)), duration, out value)
    }.ToFrozenDictionary();

    // Only formats with the full date: a parse that has to complete the date takes it from the clock of the machine.
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd' 'HH:mmK",
        "yyyy-MM-dd' 'HH:mm:ss.FFFFFFFK"
    ];

    private static readonly string[] TimeOfDayFormats = ["HH:mm", "HH:mm:ss.FFFFFFF"];

    private readonly List<string> _display = [];

    private delegate bool ConstantParser(string text, out object? value);

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

    /// <summary>Binds one text to a value of the type, which accepts null when it is a reference or a nullable value type.</summary>
    public bool TryBindValue(string name, string text, Type type, out object? value, [NotNullWhen(false)] out string? error) =>
        TryBindValue(name, text, type, acceptsNull: AcceptsNull(type), out value, out error);

    /// <summary>
    /// Binds one text to a value of the property, which accepts null when its type is a nullable value type or a reference
    /// type that the property does not annotate as non-nullable, or any reference type in an app that cannot read annotations.
    /// </summary>
    public bool TryBindProperty(RegisteredSubjectProperty property, string text, out object? value, [NotNullWhen(false)] out string? error) =>
        TryBindValue(property.Name, text, property.Type, acceptsNull: AcceptsNull(property), out value, out error);

    private bool TryBindValue(string name, string text, Type type, bool acceptsNull, out object? value, [NotNullWhen(false)] out string? error)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        if (targetType == typeof(string))
        {
            return TryRender(name, text, out value, out error);
        }

        if (acceptsNull && string.IsNullOrWhiteSpace(text))
        {
            value = null;
        }
        else if (!TryParseConstant(text, targetType, out value) && !TryEvaluate(name, text, type, out value, out error))
        {
            return false;
        }

        if (value is TimeSpan duration && !AutomationDuration.IsWithinMaximum(duration))
        {
            value = null;
            error = $"The argument '{name}' is longer than {AutomationDuration.Format(AutomationDuration.Maximum)}";
            return false;
        }

        _display.Add($"{name}: {(value is null ? "null" : Format(value))}");
        error = null;
        return true;
    }

    private static bool AcceptsNull(Type type) => MemberNullability.AcceptsNull(type);

    // A dynamic property has no PropertyInfo and so no annotation.
    private static bool AcceptsNull(RegisteredSubjectProperty property) =>
        property.Type.IsValueType || property.Reference.Metadata.PropertyInfo is not { } propertyInfo
            ? AcceptsNull(property.Type)
            : MemberNullability.AcceptsNull(propertyInfo);

    private bool TryParseConstant(string text, Type type, out object? value)
    {
        if (ConstantParsers.TryGetValue(type, out var parse))
        {
            return parse(text, out value);
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return TryParseInstant(text, type, out value);
        }

        return type.IsEnum
            ? ParameterConverter.TryConvertValue(text, type, out value)
            : ParameterConverter.TryConvert(text, type, out value);
    }

    private bool TryParseInstant(string text, Type type, out object? value)
    {
        value = null;
        DateTimeOffset instant;
        if (TimeOnly.TryParseExact(text, TimeOfDayFormats, Culture, DateTimeStyles.AllowWhiteSpaces, out var timeOfDay))
        {
            instant = new DateTimeOffset(evaluation.Now.UtcDateTime.Date + timeOfDay.ToTimeSpan(), TimeSpan.Zero);
        }
        // An offset that moves the instant beyond the range of DateTime fails here instead of wrapping around.
        else if (!DateTimeOffset.TryParseExact(text, InstantFormats, Culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out instant))
        {
            return false;
        }

        value = type == typeof(DateTime) ? (object)instant.UtcDateTime : instant;
        return true;
    }

    private static string Format(object? value) =>
        value is TimeSpan duration ? AutomationDuration.Format(duration) : RawValueFormatter.Format(value);

    private static bool TryValidateKeys(
        MethodParameter[] inputs, IReadOnlyDictionary<string, string> arguments, [NotNullWhen(false)] out string? error)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in arguments.Keys)
        {
            if (!keys.Add(key))
            {
                error = $"The argument '{key}' is given twice";
                return false;
            }

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
            var acceptsNull = parameter.IsNullable || Nullable.GetUnderlyingType(parameter.Type) is not null;
            return TryBindValue(parameter.Name, text, parameter.Type, acceptsNull, out value, out error);
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
        var members = new Dictionary<string, object?>();
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

            members[member.Name] = memberValue;
        }

        if (ParameterConverter.TryCreateObject(parameter.Type, members, out value, out var createError))
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
        if (!AutomationExpressionCheck.TryParseExpression(text, allowsEvent, out var expression, out var parseError))
        {
            error = $"The argument '{name}' is invalid: {parseError}";
            return false;
        }

        if (evaluation.Evaluate(expression).Value is not { } result)
        {
            error = DescribeUnknown(name);
            return false;
        }

        if (!ParameterConverter.TryConvertValue(result, type, out value))
        {
            error = $"The argument '{name}' cannot be converted to {(Nullable.GetUnderlyingType(type) ?? type).Name}: {RawValueFormatter.Format(result)}";
            return false;
        }

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

    private static bool Box<T>(bool isParsed, T parsed, out object? value)
    {
        value = isParsed ? parsed : null;
        return isParsed;
    }
}
