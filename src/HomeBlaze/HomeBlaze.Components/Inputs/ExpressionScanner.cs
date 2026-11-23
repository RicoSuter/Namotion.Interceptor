namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Reads an expression character by character and tells which characters stand outside quotes and the brackets of a
/// path, as the expression parser reads them: a quote inside brackets is part of the key, and a bracket inside a quote
/// is text.
/// </summary>
public struct ExpressionScanner
{
    private char _quote;
    private int _bracketDepth;

    /// <summary>Whether a quote or a bracket is open after the characters read so far.</summary>
    public readonly bool IsOpen => _quote != '\0' || _bracketDepth != 0;

    /// <summary>Reads the next character of the expression.</summary>
    /// <returns>True when the character stands outside quotes and brackets; false for a quote or a bracket and what it encloses.</returns>
    public bool Read(char character)
    {
        if (_quote != '\0')
        {
            if (character == _quote)
            {
                _quote = '\0';
            }

            return false;
        }

        if (character is '"' or '\'' && _bracketDepth == 0)
        {
            _quote = character;
            return false;
        }

        if (character == '[')
        {
            _bracketDepth++;
            return false;
        }

        if (character == ']' && _bracketDepth > 0)
        {
            _bracketDepth--;
            return false;
        }

        return _bracketDepth == 0;
    }
}
