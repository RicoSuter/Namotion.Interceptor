using System.Collections.Immutable;
using System.Globalization;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// The state of an <see cref="ExpressionField"/>: the expression text, which is what the field stores, and while the
/// expression has the simple form, its rows. A text without that form is shown advanced and never rewritten. A row
/// without a path or a value is not finished and stays out of the text until it has both.
/// </summary>
internal sealed class ExpressionFieldModel
{
    private static readonly ImmutableArray<string> DefaultVariables = [ExpressionVariables.Value, ExpressionVariables.Subject];

    private List<ExpressionRow> _rows = [];
    private List<RowSlot> _slots = [];
    private string _ownerValue;

    /// <param name="text">The expression text.</param>
    public ExpressionFieldModel(string? text)
    {
        Load(text);
        _ownerValue = Text;
    }

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

    /// <summary>The rows the field shows: those of <see cref="Text"/> and, in their places, the rows that are not finished, which it leaves out.</summary>
    public IReadOnlyList<ExpressionRow> Rows => _rows;

    /// <summary>
    /// Whether a row holds an edit that is not part of <see cref="Text"/>, because it is invalid or not finished, or a
    /// row has no path or no value yet.
    /// </summary>
    public bool HasPendingRow =>
        _slots.Exists(static slot => slot.Pending is not null) || !_rows.TrueForAll(IsFinished);

    /// <summary>
    /// Takes the value of the owner of the field and loads it only when it differs from both the value the owner passed
    /// last and <see cref="Text"/>. A render can still pass the value from before the latest edit, and loading that would
    /// undo the edit and drop the rows that are not finished.
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
        _slots = CreateSlots(_rows.Count);
        if (!CanShowSimple)
        {
            IsAdvanced = true;
        }
    }

    /// <summary>
    /// Shows the text. The pending edits and the rows that are not finished are not part of it, so they are dropped.
    /// </summary>
    /// <returns>Whether an edit or a row was dropped.</returns>
    public bool ShowAdvanced()
    {
        var isDropping = HasPendingRow;
        if (isDropping)
        {
            Load(Text);
        }

        IsAdvanced = true;
        return isDropping;
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
    public ExpressionRow GetDisplayRow(int index) => _slots[index].Pending?.Row ?? _rows[index];

    /// <summary>The pending edit of the row with its errors, or null when the row is the one of the text.</summary>
    public PendingRow? GetPendingRow(int index) => _slots[index].Pending;

    /// <summary>
    /// The identity of the row at the index, which stays with the row while rows before it are added or removed and is
    /// never given to another row, so that an edit sent for a row that is gone changes nothing.
    /// </summary>
    public object GetRowKey(int index) => _slots[index];

    /// <summary>Takes an edited row like <see cref="SetRow(int, ExpressionRow)"/>; does nothing when the row with the key is gone.</summary>
    /// <returns>Whether <see cref="Text"/> changed.</returns>
    public bool SetRow(object key, ExpressionRow row) => IndexOf(key) is var index and >= 0 && SetRow(index, row);

    /// <summary>Removes the row with the key; does nothing when it is gone.</summary>
    /// <returns>Whether <see cref="Text"/> changed.</returns>
    public bool RemoveRow(object key) => IndexOf(key) is var index and >= 0 && RemoveRow(index);

    /// <summary>
    /// Takes an edited row. A typed value that is not one token is written as text in quotes, so that it cannot change
    /// the structure of the expression; a value the row already has is written as it is. A path or value that cannot be
    /// written as typed stays a pending edit of the row, with its error when it is invalid, until a later edit of the row
    /// is valid: <see cref="Text"/> does not change. A path that is not finished, such as a lone separator, is pending
    /// without an error. An applied edit that changes the row writes every row again, so the white space and the case of
    /// the combinators of the other rows take their written form; their meaning stays the same.
    /// </summary>
    /// <returns>Whether <see cref="Text"/> changed.</returns>
    public bool SetRow(int index, ExpressionRow row)
    {
        var pending = Check(row, _rows[index], out var applied);
        if (pending is not null)
        {
            _slots[index].Pending = pending;
            return false;
        }

        var isChanged = applied != _rows[index];
        _rows[index] = applied;
        _slots[index].Pending = null;
        return isChanged && Emit();
    }

    /// <summary>Adds an empty row, which stays out of <see cref="Text"/> until it has a path and a value.</summary>
    public void AddRow()
    {
        _rows.Add(ExpressionRow.Empty(_rows.Count == 0 ? string.Empty : ExpressionRows.Combinators[0]));
        _slots.Add(new RowSlot());
    }

    /// <returns>Whether <see cref="Text"/> changed.</returns>
    public bool RemoveRow(int index)
    {
        _rows.RemoveAt(index);
        _slots.RemoveAt(index);
        if (index == 0 && _rows.Count > 0)
        {
            _rows[0] = _rows[0] with { Combinator = string.Empty };
            if (_slots[0].Pending is { } first)
            {
                _slots[0].Pending = first with { Row = first.Row with { Combinator = string.Empty } };
            }
        }

        return Emit();
    }

    private bool Emit()
    {
        var previous = Text;
        Text = ExpressionRows.Format(_rows.TrueForAll(IsFinished) ? _rows : _rows.FindAll(IsFinished));
        CanShowSimple = true;
        return !string.Equals(previous, Text, StringComparison.Ordinal);
    }

    private static bool IsFinished(ExpressionRow row) => row.Path.Length > 0 && row.Value.Length > 0;

    // Null when the row can be written into the text as the applied row. Only a value that differs from the one the text
    // holds is normalized: a loaded bare path such as Sibling/Value is a valid relative lookup, and quoting it when another
    // part of the row changes would turn a comparison against a live value into a comparison against text.
    private PendingRow? Check(ExpressionRow row, ExpressionRow current, out ExpressionRow applied)
    {
        applied = row;
        var path = row.Path.Trim();
        if (path.Length > 0 && !IsFinishedPath(path))
        {
            return new PendingRow(row, IsUnfinishedPath(path) ? null : $"Not a valid path: {path}", null);
        }

        var value = row.Value.Trim();
        if (value != current.Value)
        {
            var (normalized, valueError) = NormalizeValue(value);
            if (valueError is not null)
            {
                return new PendingRow(row, null, valueError);
            }

            value = normalized;
        }

        applied = row with { Path = path, Value = value };
        return null;
    }

    private bool IsFinishedPath(string path) =>
        !IsUnfinishedPath(path) && IsSingleToken(path) && ExpressionRows.IsPath(path) && ParsesText($"{path} == 1");

    private bool IsUnfinishedPath(string path) =>
        path.EndsWith(ExpressionRows.PathSeparator) || path is "." or ".." || path.Count(c => c == '[') > path.Count(c => c == ']');

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
        (value.Contains(ExpressionRows.PathSeparator) || value.Contains('[')) &&
        !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
        !StartsLikePath(value);

    // A leading minus negates the path after it.
    private bool StartsLikePath(string value)
    {
        var path = value.StartsWith('-') ? value[1..] : value;
        return path.StartsWith(ExpressionRows.PathSeparator) || path.StartsWith("./") || path.StartsWith("../") ||
            (ParsedExpression.TryParse(path, ExpressionRows.PathSeparator, Variables, out var expression, out _) &&
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

    private bool ParsesText(string text) => ParsedExpression.TryParse(text, ExpressionRows.PathSeparator, Variables, out _, out _);

    private static bool StartsLikeNumber(string value) =>
        char.IsDigit(value[0]) || (value[0] is '-' or '+' or '.' && value.Length > 1);

    private static bool IsInstantOrTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _);

    private static bool IsSingleToken(string text) =>
        ExpressionTokens.Split(text) is [var token] && token == text;

    private int IndexOf(object key) => _slots.FindIndex(slot => ReferenceEquals(slot, key));

    private static List<RowSlot> CreateSlots(int count)
    {
        var slots = new List<RowSlot>(count);
        for (var index = 0; index < count; index++)
        {
            slots.Add(new RowSlot());
        }

        return slots;
    }

    // One per row: its identity, and its edit that is not part of the text.
    private sealed class RowSlot
    {
        public PendingRow? Pending { get; set; }
    }

    /// <summary>An edit of a row that is not part of <see cref="Text"/>, with the error of its path or value when it is invalid.</summary>
    public sealed record PendingRow(ExpressionRow Row, string? PathError, string? ValueError);
}
