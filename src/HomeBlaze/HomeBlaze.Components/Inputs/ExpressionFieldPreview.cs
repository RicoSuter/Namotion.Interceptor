using System.Collections.Immutable;
using HomeBlaze.Components.Abstractions.Expressions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// The texts an <see cref="ExpressionField"/> shows about its expression: the error, the hints, the result and the current
/// value of each row. <see cref="Update"/> evaluates them only when the text, the rows or the delegates changed and
/// prepares the expression, the rows and the hints for evaluation once, so that <see cref="RefreshValues"/> reads the
/// current values again without parsing anything, and a render evaluates nothing. The hints of the host depend on the
/// text only, so a refresh does not ask for them again.
/// </summary>
internal sealed class ExpressionFieldPreview
{
    private const string NoValueText = "no value";

    private string? _text;
    private ExpressionRow[] _rows = [];
    private ImmutableArray<string> _variables;
    private Func<string, string?>? _validate;
    private Func<string, Func<ExpressionPreview>?>? _preview;
    private Func<string, ImmutableArray<string>>? _getHints;
    private ImmutableArray<string> _hostHints = [];
    private Func<ExpressionPreview>? _result;
    private RowReader[] _rowReaders = [];
    private string[] _rowTexts = [];
    private HintReader[] _hintReaders = [];
    private bool _areHintsStale;

    public string? Error { get; private set; }

    public IReadOnlyList<string> Hints { get; private set; } = [];

    /// <summary>The result of the expression with the current values, or null when it has none to show.</summary>
    public string? ResultText { get; private set; }

    /// <summary>The current value of each row and its result, empty for a row without a path or without a preview.</summary>
    public IReadOnlyList<string> RowTexts => _rowTexts;

    /// <summary>
    /// Evaluates the texts when anything they depend on changed. <paramref name="preview"/> prepares an expression or a
    /// path for evaluation with the current values, or returns null when it cannot be evaluated.
    /// </summary>
    public void Update(
        string text,
        IReadOnlyList<ExpressionRow> rows,
        ImmutableArray<string> variables,
        Func<string, string?>? validate,
        Func<string, Func<ExpressionPreview>?>? preview,
        Func<string, ImmutableArray<string>>? getHints = null)
    {
        if (_text == text && _variables == variables && Equals(_validate, validate) &&
            Equals(_preview, preview) && Equals(_getHints, getHints) && HasRows(rows))
        {
            return;
        }

        _text = text;
        _rows = [.. rows];
        _variables = variables;
        _validate = validate;
        _preview = preview;
        _getHints = getHints;
        var hasText = !string.IsNullOrWhiteSpace(text);
        Error = hasText ? validate?.Invoke(text) : null;
        _hostHints = Error is null && hasText && getHints is not null ? getHints(text) : [];
        _result = hasText && Error is null ? preview?.Invoke(text) : null;
        _rowReaders = new RowReader[_rows.Length];
        _rowTexts = new string[_rows.Length];
        for (var index = 0; index < _rows.Length; index++)
        {
            _rowReaders[index] = new RowReader(_rows[index], preview);
            _rowTexts[index] = string.Empty;
        }

        var hints = ExpressionHints.Find(text, variables);
        _hintReaders = new HintReader[hints.Count];
        for (var index = 0; index < hints.Count; index++)
        {
            _hintReaders[index] = new HintReader(hints[index], preview);
        }

        _areHintsStale = true;
        ResultText = null;
        Hints = [];
        Evaluate();
    }

    private bool HasRows(IReadOnlyList<ExpressionRow> rows)
    {
        if (_rows.Length != rows.Count)
        {
            return false;
        }

        for (var index = 0; index < _rows.Length; index++)
        {
            if (_rows[index] != rows[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads the current values again and returns whether a text changed.</summary>
    public bool RefreshValues() => _preview is not null && _text is not null && Evaluate();

    private bool Evaluate()
    {
        var isChanged = false;
        var resultText = _result is { } result ? FormatResult(result().Result) : null;
        if (!ReferenceEquals(resultText, ResultText))
        {
            ResultText = resultText;
            isChanged = true;
        }

        for (var index = 0; index < _rowReaders.Length; index++)
        {
            if (_rowReaders[index].Refresh())
            {
                _rowTexts[index] = _rowReaders[index].Text;
                isChanged = true;
            }
        }

        var areHintsChanged = _areHintsStale;
        foreach (var hintReader in _hintReaders)
        {
            areHintsChanged |= hintReader.Refresh();
        }

        if (areHintsChanged)
        {
            _areHintsStale = false;
            var hints = new List<string>(_hostHints);
            foreach (var hintReader in _hintReaders)
            {
                if (hintReader.IsShown)
                {
                    hints.Add(hintReader.Hint.Text);
                }
            }

            if (!hints.SequenceEqual(Hints))
            {
                Hints = hints;
                isChanged = true;
            }
        }

        return isChanged;
    }

    // The result texts are constants, so a changed text is a different reference.
    private static string FormatResult(bool? result) => result switch
    {
        true => "true",
        false => "false",
        null => "unknown"
    };

    // The value and the result of a row, with the text built again only when one of them changed.
    private sealed class RowReader
    {
        private readonly Func<ExpressionPreview>? _value;
        private readonly Func<ExpressionPreview>? _condition;
        private string? _valueText;
        private bool? _conditionResult;

        public RowReader(ExpressionRow row, Func<string, Func<ExpressionPreview>?>? preview)
        {
            if (preview is null || string.IsNullOrWhiteSpace(row.Path))
            {
                return;
            }

            _value = preview(row.Path) ?? NoValue;
            _condition = string.IsNullOrWhiteSpace(row.Value) ? null : preview(row.ToCondition());
        }

        public string Text { get; private set; } = string.Empty;

        /// <summary>Reads the value and the result again and returns whether <see cref="Text"/> changed.</summary>
        public bool Refresh()
        {
            if (_value is null)
            {
                return false;
            }

            var valueText = _value().Text is { Length: > 0 } text ? text : NoValueText;
            var conditionResult = _condition?.Invoke().Result;
            if (Text.Length > 0 && valueText == _valueText && conditionResult == _conditionResult)
            {
                return false;
            }

            _valueText = valueText;
            _conditionResult = conditionResult;
            Text = _condition is null ? $"now {valueText}" : $"now {valueText}, {FormatResult(conditionResult)}";
            return true;
        }

        private static ExpressionPreview NoValue() => new(null, null, null);
    }

    // A hint and the value that decides whether it is shown.
    private sealed class HintReader
    {
        private readonly Func<ExpressionPreview>? _value;
        private readonly bool _hasValues;

        public HintReader(ExpressionHint hint, Func<string, Func<ExpressionPreview>?>? preview)
        {
            Hint = hint;
            _hasValues = preview is not null;
            _value = hint.Path is not null && preview is not null ? preview(hint.Path) : null;
        }

        public ExpressionHint Hint { get; }

        public bool IsShown { get; private set; }

        /// <summary>Reads the value again and returns whether <see cref="IsShown"/> changed.</summary>
        public bool Refresh()
        {
            var isShown = Hint.IsShown(_value?.Invoke().Value, _hasValues);
            if (isShown == IsShown)
            {
                return false;
            }

            IsShown = isShown;
            return true;
        }
    }
}
