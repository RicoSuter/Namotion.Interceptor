using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Types index text by the container it addresses. A collection reads a canonical position. A dictionary with string,
/// integer, <see cref="Guid"/> or enum keys parses the text to its key type, accepting only the text the writer
/// emits. Any other dictionary matches the text against the written text of its registered entries.
/// </summary>
internal static class PathIndexResolver
{
    /// <param name="property">The property the segment names.</param>
    /// <param name="text">The unquoted index text.</param>
    /// <param name="textString"><paramref name="text"/> as a string when the caller already holds one, otherwise null.</param>
    /// <param name="resolveChild">Whether to look up the subject stored at the key.</param>
    /// <param name="key">The typed key.</param>
    /// <param name="child">The subject stored at the key, or null (always null unless <paramref name="resolveChild"/>).</param>
    /// <returns>False when the property is neither a subject collection nor a subject dictionary, or the text is not a key of it.</returns>
    public static bool TryResolve(
        RegisteredSubjectProperty property,
        ReadOnlySpan<char> text,
        string? textString,
        bool resolveChild,
        [NotNullWhen(true)] out object? key,
        out IInterceptorSubject? child)
    {
        child = null;

        if (property.IsSubjectCollection)
        {
            if (!TryParsePosition(text, out var position))
            {
                key = null;
                return false;
            }

            key = position;
            if (resolveChild && property.GetValue() is { } collection)
            {
                child = SubjectLookup.FindSubjectInCollection(collection, position);
            }

            return true;
        }

        if (property.IsSubjectDictionary)
        {
            var dictionary = property.GetValue();
            var keyType = SubjectLookup.GetDictionaryKeyType(dictionary?.GetType() ?? property.Type);
            object? parsedKey = null;
            switch (keyType is null ? null : TryParseKey(keyType, text, textString, out parsedKey))
            {
                case true:
                    key = parsedKey!;
                    if (resolveChild && dictionary is not null)
                    {
                        child = SubjectLookup.FindSubjectInDictionary(dictionary, key);
                    }

                    return true;

                case false:
                    key = null;
                    return false;

                default:
                    key = MatchWrittenKey(property, text, out var matched) ?? textString ?? text.ToString();
                    child = resolveChild ? matched : null;
                    return true;
            }
        }

        key = null;
        return false;
    }

    private static bool TryParsePosition(ReadOnlySpan<char> text, out int position)
    {
        // Canonical digits only: a sign, whitespace or a leading zero is text the writer never emits.
        if (text.Length > 1 && text[0] == '0')
        {
            position = 0;
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out position);
    }

    /// <returns>True when parsed, false when the key type is supported but the text is not its written form, null when the key type is not supported.</returns>
    private static bool? TryParseKey(Type keyType, ReadOnlySpan<char> text, string? textString, out object? key)
    {
        key = null;
        if (keyType.IsEnum)
        {
            // The written-form check rejects numeric forms of named values and alias names, which the writer never emits.
            if (Enum.TryParse(keyType, text, ignoreCase: false, out var value) && IsWrittenForm((ISpanFormattable)value!, text))
            {
                key = value;
            }

            return key is not null;
        }

        if (keyType == typeof(Guid))
        {
            if (Guid.TryParse(text, out var value) && IsWrittenForm(value, text))
            {
                key = value;
            }

            return key is not null;
        }

        switch (Type.GetTypeCode(keyType))
        {
            case TypeCode.String: key = textString ?? text.ToString(); break;
            case TypeCode.SByte: key = ParseInteger<sbyte>(text); break;
            case TypeCode.Byte: key = ParseInteger<byte>(text); break;
            case TypeCode.Int16: key = ParseInteger<short>(text); break;
            case TypeCode.UInt16: key = ParseInteger<ushort>(text); break;
            case TypeCode.Int32: key = ParseInteger<int>(text); break;
            case TypeCode.UInt32: key = ParseInteger<uint>(text); break;
            case TypeCode.Int64: key = ParseInteger<long>(text); break;
            case TypeCode.UInt64: key = ParseInteger<ulong>(text); break;
            default: return null;
        }

        return key is not null;
    }

    private static object? ParseInteger<T>(ReadOnlySpan<char> text) where T : struct, IBinaryInteger<T>
        => T.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && IsWrittenForm(value, text)
            ? value
            : null;

    /// <summary>
    /// Finds the registered entry whose written key text equals <paramref name="text"/>. Reads the registered children,
    /// which reflect the last assignment of the dictionary.
    /// </summary>
    private static object? MatchWrittenKey(RegisteredSubjectProperty property, ReadOnlySpan<char> text, out IInterceptorSubject? child)
    {
        foreach (var entry in property.Children)
        {
            if (entry.Index is { } entryKey && PathSyntax.KeyTextEquals(entryKey, text))
            {
                child = entry.Subject;
                return entryKey;
            }
        }

        child = null;
        return null;
    }

    private static bool IsWrittenForm<T>(T value, ReadOnlySpan<char> text) where T : ISpanFormattable
    {
        Span<char> buffer = stackalloc char[64];
        return value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture)
            ? text.SequenceEqual(buffer[..written])
            : text.SequenceEqual(value.ToString(null, CultureInfo.InvariantCulture));
    }
}
