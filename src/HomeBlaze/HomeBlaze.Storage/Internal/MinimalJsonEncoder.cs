using System.Text;
using System.Text.Encodings.Web;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Escapes only what JSON requires: quotes, backslashes and control characters. Every other character, one outside
/// the Basic Multilingual Plane included, is written as it is.
/// </summary>
internal sealed class MinimalJsonEncoder : JavaScriptEncoder
{
    private MinimalJsonEncoder()
    {
    }

    public static MinimalJsonEncoder Instance { get; } = new();

    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) => unicodeScalar is < 0x20 or '"' or '\\';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var index = 0; index < textLength; index++)
        {
            if (WillEncode(text[index]))
            {
                return index;
            }
        }

        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var destination = new Span<char>(buffer, bufferLength);
        if (!WillEncode(unicodeScalar))
        {
            return new Rune(unicodeScalar).TryEncodeToUtf16(destination, out numberOfCharactersWritten);
        }

        var shortEscape = unicodeScalar switch
        {
            '"' => '"',
            '\\' => '\\',
            '\b' => 'b',
            '\t' => 't',
            '\n' => 'n',
            '\f' => 'f',
            '\r' => 'r',
            _ => '\0'
        };

        return shortEscape != '\0'
            ? destination.TryWrite($"\\{shortEscape}", out numberOfCharactersWritten)
            : destination.TryWrite($"\\u{unicodeScalar:X4}", out numberOfCharactersWritten);
    }
}
