using System.Collections.Immutable;
using System.Globalization;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// The state of an <see cref="ExpressionField"/>: the expression text, which is what the field stores, and while the
/// expression has the simple form, its rows. A text without that form is shown advanced and never rewritten.
/// </summary>
internal sealed class ExpressionFieldModel
{
    private static readonly ImmutableArray<string> DefaultVariables = [ExpressionVariables.Value, ExpressionVariables.Subject];

    private List<ExpressionRow> _rows = [];
    private List<PendingRow?> _pending = [];
    private string _ownerValue;

    /// <param name="text">The expression text.</param>
    public ExpressionFieldModel(string? text)
    {
        Load(text);
        IsAdvanced = !CanShowSimple;
        _ownerValue = Text;
    }

    public char PathSeparator { get; } = '/';

    /// <summary>The variables the host declares; default stands for <c>value</c> and <c>subject</c>.</summary>
    public ImmutableArray<string> Variables
    {
        get;
        set => field = value.IsDefault ? DefaultVariables : value;
    } = DefaultVariables;

    public string Text { get; private set; } = string.Empty;

    public bool IsAdvanced { get; private set; }

    /// <summary>Whether the text has the simple form, so that the rows can show it.</summary>
    public bool CanShowSimple { get; private set; }

    /// <summary>The rows as they are written in <see cref="Text"/>.</summary>
    public IReadOnlyList<ExpressionRow> Rows => _rows;

    /// <summary>Whether a row holds an edit that is not part of <see cref="Text"/>, because it is invalid or not finished.</summary>
    public bool HasPendingRow => _pending.Exists(pending => pending is not null);

    /// <summary>
    /// Takes the value of the owner of the field and loads it only when it differs from both the value the owner passed
    /// last and <see cref="Text"/>. A render can still pass the value from before the latest edit, and loading that would
    /// undo the edit and show a row that is not finished as advanced text.
    /// </summary>
    public void SetValue(string? value)
    {
        value ??= string.Empty;
        if (string.Equals(value, _ownerValue, StringComparison.Ordinal))
        {
            return;
        }

        _ownerValue = value;
        if (!string.Equals(value, Text, StringComparison.Ordinal))
        {
            Load(value);
        }
    }

    /// <summary>Takes a text from outside the rows: from the owner of the field or from the advanced input. Never rewrites it.</summary>
    public void Load(string? text)
    {
        Text = text ?? string.Empty;
        CanShowSimple = ExpressionRows.TryParse(Text, out var rows);
        _rows = [.. rows];
        _pending = [.. _rows.Select(_ => (PendingRow?)null)];
        if (!CanShowSimple)
        {
            IsAdvanced = true;
        }
    }

    public void ShowAdvanced()
    {
        IsAdvanced = true;
        _pending = [.. _rows.Select(_ => (PendingRow?)null)];
    }

    /// <summary>Shows the rows; false when the text does not have the simple form.</summary>
    public bool ShowSimple()
    {
        if (!CanShowSimple)
        {
            return false;
        }

        IsAdvanced = false;
        return true;
    }

    /// <summary>The row as the user sees it: the pending edit when there is one, otherwise the row of the text.</summary>
    public ExpressionRow GetDisplayRow(int index) => _pending[index]?.Row ?? _rows[index];

    public string? GetPathError(int index) => _pending[index]?.PathError;

    public string? GetValueError(int index) => _pending[index]?.ValueError;

    /// <summary>
    /// Takes an edited row. A typed value that is not one token is written as text in quotes, so that it cannot change
    /// the structure of the expression. A path or value that cannot be written as typed stays a pending edit of the row,
    /// with its error when it is invalid, until a later edit of the row is valid: <see cref="Text"/> does not change.
    /// A path that is not finished, such as a lone separator, is pending without an error.
    /// </summary>
    public void SetRow(int index, ExpressionRow row)
    {
        var check = Check(row);
        if (check.IsApplicable)
        {
            _rows[index] = check.Row;
            _pending[index] = null;
            Emit();
            return;
        }

        _pending[index] = new PendingRow(row, check.PathError, check.ValueError);
    }

    public void AddRow()
    {
        _rows.Add(ExpressionRow.Empty(_rows.Count == 0 ? string.Empty : ExpressionRows.Combinators[0]));
        _pending.Add(null);
        Emit();
    }

    public void RemoveRow(int index)
    {
        _rows.RemoveAt(index);
        _pending.RemoveAt(index);
        if (index == 0 && _rows.Count > 0)
        {
            _rows[0] = _rows[0] with { Combinator = string.Empty };
            _pending[0] = _pending[0] is { } first ? first with { Row = first.Row with { Combinator = string.Empty } } : null;
        }

        Emit();
    }

    // A row that is still being filled in is kept as it is, although its text does not parse yet.
    private void Emit()
    {
        Text = ExpressionRows.Format(_rows);
        CanShowSimple = true;
    }

    private RowCheck Check(ExpressionRow row)
    {
        var path = row.Path.Trim();
        if (path.Length > 0 && !IsFinishedPath(path))
        {
            return new RowCheck(false, row, IsUnfinishedPath(path) ? null : $"Not a valid path: {path}", null);
        }

        var (value, valueError) = NormalizeValue(row.Value.Trim());
        return valueError is null
            ? new RowCheck(true, row with { Path = path, Value = value }, null, null)
            : new RowCheck(false, row, null, valueError);
    }

    private bool IsFinishedPath(string path) =>
        !IsUnfinishedPath(path) && IsSingleToken(path) && ExpressionRows.IsPath(path) && ParsesText($"{path} == 1");

    private bool IsUnfinishedPath(string path) =>
        path.EndsWith(PathSeparator) || path is "." or ".." || path.Count(c => c == '[') > path.Count(c => c == ']');

    private (string Value, string? Error) NormalizeValue(string value)
    {
        if (value.Length == 0)
        {
            return (value, null);
        }

        if (!IsSingleToken(value) || !ExpressionRows.IsOperand(value) || IsPathLikeText(value))
        {
            return Quote(value);
        }

        if (Parses(value))
        {
            return (value, null);
        }

        if (value.Contains(',') && Parses(value.Replace(',', '.')))
        {
            return (value, "Use '.' as the decimal point.");
        }

        if (!StartsLikeNumber(value))
        {
            return Quote(value);
        }

        return IsInstantOrTime(value) ? Quote(value) : (value, $"Not a valid number: {value}. Put text in quotes.");
    }

    // A token with the path separator or an index is a path lookup unless it starts like a path.
    private bool IsPathLikeText(string value) =>
        (value.Contains(PathSeparator) || value.Contains('[')) &&
        !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
        !StartsLikePath(value);

    // A leading minus negates the path after it.
    private bool StartsLikePath(string value)
    {
        var path = value.StartsWith('-') ? value[1..] : value;
        return path.StartsWith(PathSeparator) || path.StartsWith("." + PathSeparator) || path.StartsWith(".." + PathSeparator) ||
            (ParsedExpression.TryParse(path, PathSeparator, Variables, out var expression, out _) &&
             expression.SinglePath is { } singlePath &&
             expression.TryGetVariable(singlePath, out _, out _));
    }

    private (string Value, string? Error) Quote(string value)
    {
        if (value.Contains('"') && value.Contains('\''))
        {
            return (value, "A value cannot hold both kinds of quotes.");
        }

        var quote = value.Contains('"') ? '\'' : '"';
        return ($"{quote}{value}{quote}", null);
    }

    private bool Parses(string operand) => ParsesText($"x == {operand}");

    private bool ParsesText(string text) => ParsedExpression.TryParse(text, PathSeparator, Variables, out _, out _);

    private static bool StartsLikeNumber(string value) =>
        char.IsDigit(value[0]) || (value[0] is '-' or '+' or '.' && value.Length > 1);

    private static bool IsInstantOrTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _);

    private static bool IsSingleToken(string text) =>
        ExpressionTokens.Split(text) is [var token] && token == text;

    private sealed record PendingRow(ExpressionRow Row, string? PathError, string? ValueError);

    private sealed record RowCheck(bool IsApplicable, ExpressionRow Row, string? PathError, string? ValueError);
}
