namespace HomeBlaze.Components.Inputs;

/// <summary>
/// The state of an <see cref="ExpressionField"/>: the expression text, which is what the field stores, and while the
/// expression has the simple form, its rows. A text without that form is shown advanced and never rewritten.
/// </summary>
internal sealed class ExpressionFieldModel
{
    private List<ExpressionRow> _rows = [];

    public ExpressionFieldModel(string? text)
    {
        Load(text);
        IsAdvanced = !CanShowSimple;
    }

    public string Text { get; private set; } = string.Empty;

    public bool IsAdvanced { get; private set; }

    /// <summary>Whether the text has the simple form, so that the rows can show it.</summary>
    public bool CanShowSimple { get; private set; }

    public IReadOnlyList<ExpressionRow> Rows => _rows;

    /// <summary>Why the last row edit was not taken, or null when it was.</summary>
    public string? Error { get; private set; }

    /// <summary>Takes a text from outside the rows: from the owner of the field or from the advanced input. Never rewrites it.</summary>
    public void Load(string? text)
    {
        Text = text ?? string.Empty;
        Error = null;
        CanShowSimple = ExpressionRows.TryParse(Text, out var rows);
        _rows = [.. rows];
        if (!CanShowSimple)
        {
            IsAdvanced = true;
        }
    }

    public void ShowAdvanced() => IsAdvanced = true;

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

    /// <summary>
    /// Takes an edited row. A typed value that is not one token is written as text in quotes, so that it cannot change
    /// the structure of the expression; a path that is not one path, or a value that cannot be quoted, is rejected with
    /// <see cref="Error"/> and the row stays as it was.
    /// </summary>
    public void SetRow(int index, ExpressionRow row)
    {
        var error = Normalize(row, out var normalized);
        Error = error;
        if (error is null)
        {
            _rows[index] = normalized;
            Emit();
        }
    }

    public void AddRow()
    {
        Error = null;
        _rows.Add(ExpressionRow.Empty(_rows.Count == 0 ? string.Empty : ExpressionRows.Combinators[0]));
        Emit();
    }

    public void RemoveRow(int index)
    {
        Error = null;
        _rows.RemoveAt(index);
        if (index == 0 && _rows.Count > 0)
        {
            _rows[0] = _rows[0] with { Combinator = string.Empty };
        }

        Emit();
    }

    // A row that is still being filled in is kept as it is, although its text does not parse yet.
    private void Emit()
    {
        Text = ExpressionRows.Format(_rows);
        CanShowSimple = true;
    }

    private static string? Normalize(ExpressionRow row, out ExpressionRow normalized)
    {
        normalized = row;
        var path = row.Path.Trim();
        if (path.Length > 0 && !(IsSingleToken(path) && ExpressionRows.IsPath(path)))
        {
            return "A path is one word without spaces, brackets or operators.";
        }

        var value = row.Value.Trim();
        if (value.Length > 0 && !(IsSingleToken(value) && ExpressionRows.IsOperand(value)))
        {
            if (value.Contains('"') && value.Contains('\''))
            {
                return "A value cannot hold both kinds of quotes.";
            }

            var quote = value.Contains('"') ? '\'' : '"';
            value = quote + value + quote;
        }

        normalized = row with { Path = path, Value = value };
        return null;
    }

    private static bool IsSingleToken(string text) =>
        ExpressionTokens.Split(text) is [var token] && token == text;
}
