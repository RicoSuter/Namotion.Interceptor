using System.Collections.Immutable;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// The texts an <see cref="ExpressionField"/> shows about its expression: the error, the hints, the result and the current
/// value of each row. <see cref="Update"/> evaluates them only when the text, the rows or the delegates changed, and
/// <see cref="RefreshValues"/> reads the current values again, so that a render evaluates nothing. The hints of the host
/// depend on the text only, so a refresh does not ask for them again.
/// </summary>
internal sealed class ExpressionFieldPreview
{
    private string? _text;
    private IReadOnlyList<ExpressionRow> _rows = [];
    private char _pathSeparator;
    private ImmutableArray<string> _variables;
    private Func<string, string?>? _validate;
    private Func<string, ExpressionPreview?>? _preview;
    private Func<string, IReadOnlyList<string>>? _getHints;
    private IReadOnlyList<string> _hostHints = [];

    public string? Error { get; private set; }

    public IReadOnlyList<string> Hints { get; private set; } = [];

    /// <summary>The result of the expression with the current values, or null when it has none to show.</summary>
    public string? ResultText { get; private set; }

    /// <summary>The current value of each row and its result, empty for a row without a path or without a preview.</summary>
    public IReadOnlyList<string> RowTexts { get; private set; } = [];

    /// <summary>Evaluates the texts when anything they depend on changed.</summary>
    public void Update(
        string text,
        IReadOnlyList<ExpressionRow> rows,
        char pathSeparator,
        ImmutableArray<string> variables,
        Func<string, string?>? validate,
        Func<string, ExpressionPreview?>? preview,
        Func<string, IReadOnlyList<string>>? getHints = null)
    {
        if (_text == text && _pathSeparator == pathSeparator && _variables == variables && Equals(_validate, validate) &&
            Equals(_preview, preview) && Equals(_getHints, getHints) && _rows.SequenceEqual(rows))
        {
            return;
        }

        _text = text;
        _rows = [.. rows];
        _pathSeparator = pathSeparator;
        _variables = variables;
        _validate = validate;
        _preview = preview;
        _getHints = getHints;
        Error = string.IsNullOrWhiteSpace(text) ? null : validate?.Invoke(text);
        _hostHints = Error is null && !string.IsNullOrWhiteSpace(text) && getHints is not null ? getHints(text) : [];
        Evaluate();
    }

    /// <summary>Reads the current values again and returns whether a text changed.</summary>
    public bool RefreshValues(IReadOnlyList<ExpressionRow> rows)
    {
        if (_preview is null || _text is null)
        {
            return false;
        }

        var (hints, resultText, rowTexts) = (Hints, ResultText, RowTexts);
        _rows = [.. rows];
        Evaluate();
        return resultText != ResultText || !hints.SequenceEqual(Hints) || !rowTexts.SequenceEqual(RowTexts);
    }

    private void Evaluate()
    {
        var text = _text ?? string.Empty;
        var preview = _preview;
        var bareWordHints = ExpressionHints.GetBareWordHints(text, _pathSeparator, _variables, path => preview?.Invoke(path)?.Value);
        Hints = _hostHints.Count == 0 ? bareWordHints : [.. _hostHints, .. bareWordHints];
        ResultText = !string.IsNullOrWhiteSpace(text) && Error is null && preview?.Invoke(text) is { } result ? FormatResult(result.Result) : null;
        RowTexts = [.. _rows.Select(DescribeRow)];
    }

    private string DescribeRow(ExpressionRow row)
    {
        if (_preview is not { } preview || string.IsNullOrWhiteSpace(row.Path))
        {
            return string.Empty;
        }

        var valueText = preview(row.Path)?.Text is { Length: > 0 } text ? text : "no value";
        var condition = string.IsNullOrWhiteSpace(row.Value) ? null : preview(row.ToCondition());
        return condition is null ? $"now {valueText}" : $"now {valueText}, {FormatResult(condition.Result)}";
    }

    private static string FormatResult(bool? result) => result switch
    {
        true => "true",
        false => "false",
        null => "unknown"
    };
}
