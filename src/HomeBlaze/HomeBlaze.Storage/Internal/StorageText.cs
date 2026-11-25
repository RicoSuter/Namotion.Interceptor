using System.Text;

namespace HomeBlaze.Storage.Internal;

/// <summary>Decodes the content of a file as text and encodes text back in the same encoding.</summary>
internal static class StorageText
{
    private static readonly UTF8Encoding Utf8WithoutByteOrderMark = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Decodes the content in the encoding its byte order mark names, else UTF-8.</summary>
    public static string Decode(byte[] content, out Encoding encoding, out bool hasByteOrderMark)
    {
        // A reader, not Encoding.GetString: it drops a byte order mark, which the JSON parser rejects.
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        encoding = reader.CurrentEncoding;
        var preamble = encoding.Preamble;
        hasByteOrderMark = preamble.Length > 0 && content.AsSpan().StartsWith(preamble);
        return text;
    }

    /// <summary>Encodes the text in the encoding, with its byte order mark when the original had one.</summary>
    public static byte[] Encode(string text, Encoding encoding, bool hasByteOrderMark)
    {
        var target = encoding is UTF8Encoding && !hasByteOrderMark ? Utf8WithoutByteOrderMark : encoding;
        if (!hasByteOrderMark)
        {
            return target.GetBytes(text);
        }

        var preamble = target.Preamble;
        var result = new byte[preamble.Length + target.GetByteCount(text)];
        preamble.CopyTo(result);
        target.GetBytes(text, result.AsSpan(preamble.Length));
        return result;
    }
}
