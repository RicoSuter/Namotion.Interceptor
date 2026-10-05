using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Types index text by the container it addresses: a collection reads a position, a dictionary reads a key of
/// its own key type. Integer, Guid and enum keys only accept the text the writer emits. Other key types are
/// matched by their invariant text, and an absent key of such a type is returned as the text.
/// </summary>
internal static class PathIndexResolver
{
    private enum KeyKind
    {
        Unknown,
        String,
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Guid,
        Enum
    }

    private static readonly ConcurrentDictionary<Type, (KeyKind Kind, Type? KeyType)> KeyKinds = new();

    /// <summary>
    /// Types <paramref name="text"/> by the container <paramref name="property"/> holds.
    /// </summary>
    /// <param name="property">The collection or dictionary property.</param>
    /// <param name="text">The key text, unescaped.</param>
    /// <param name="textString"><paramref name="text"/> when it already is a string, so a string key reuses it; otherwise null.</param>
    /// <param name="key">The typed key.</param>
    /// <param name="child">The subject stored at the key, or null.</param>
    public static bool TryResolve(
        RegisteredSubjectProperty property,
        ReadOnlySpan<char> text,
        string? textString,
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
            var collection = property.GetValue();
            child = collection is null ? null : SubjectLookup.FindSubjectInCollection(collection, position);
            return true;
        }

        if (property.IsSubjectDictionary)
        {
            var dictionary = property.GetValue();
            var (kind, keyType) = GetKeyKind(dictionary?.GetType() ?? property.Type);

            if (kind == KeyKind.Unknown)
            {
                var keyText = textString ?? text.ToString();
                key = keyText;
                if (dictionary is not null)
                {
                    child = FindByKeyText(dictionary, keyText, out var matchedKey);
                    if (matchedKey is not null)
                    {
                        key = matchedKey;
                    }
                }

                return true;
            }

            key = ParseKey(kind, keyType, text, textString);
            if (key is null)
            {
                return false;
            }

            child = dictionary is null ? null : SubjectLookup.FindSubjectInDictionary(dictionary, key);
            return true;
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

    private static object? ParseKey(KeyKind kind, Type? keyType, ReadOnlySpan<char> text, string? textString) => kind switch
    {
        KeyKind.String => textString ?? text.ToString(),
        KeyKind.SByte => ParseInteger<sbyte>(text),
        KeyKind.Byte => ParseInteger<byte>(text),
        KeyKind.Int16 => ParseInteger<short>(text),
        KeyKind.UInt16 => ParseInteger<ushort>(text),
        KeyKind.Int32 => ParseInteger<int>(text),
        KeyKind.UInt32 => ParseInteger<uint>(text),
        KeyKind.Int64 => ParseInteger<long>(text),
        KeyKind.UInt64 => ParseInteger<ulong>(text),
        KeyKind.Guid => ParseGuid(text),
        KeyKind.Enum => ParseEnum(keyType!, text),
        _ => null
    };

    private static object? ParseInteger<T>(ReadOnlySpan<char> text) where T : struct, IBinaryInteger<T>
    {
        if (T.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) &&
            IsCanonical(value, text))
        {
            return value;
        }

        return null;
    }

    private static object? ParseGuid(ReadOnlySpan<char> text)
        => Guid.TryParse(text, out var value) && IsCanonical(value, text) ? value : null;

    private static object? ParseEnum(Type enumType, ReadOnlySpan<char> text)
    {
        // The canonical check rejects numeric forms of named values and alias names, which the writer never emits.
        if (Enum.TryParse(enumType, text, ignoreCase: false, out var value) &&
            value is ISpanFormattable formattable &&
            IsCanonical(formattable, text))
        {
            return value;
        }

        return null;
    }

    private static bool IsCanonical<T>(T value, ReadOnlySpan<char> text) where T : ISpanFormattable
    {
        Span<char> buffer = stackalloc char[64];
        return value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture)
            ? buffer[..written].SequenceEqual(text)
            : value.ToString(null, CultureInfo.InvariantCulture).AsSpan().SequenceEqual(text);
    }

    private static IInterceptorSubject? FindByKeyText(object dictionary, string text, out object? matchedKey)
    {
        if (dictionary is IDictionary entries)
        {
            foreach (DictionaryEntry entry in entries)
            {
                if (entry.Value is IInterceptorSubject subject && PathSyntax.FormatIndex(entry.Key) == text)
                {
                    matchedKey = entry.Key;
                    return subject;
                }
            }
        }
        else if (dictionary is IEnumerable pairs)
        {
            foreach (var pair in pairs)
            {
                if (pair is not null &&
                    SubjectLookup.TryGetSubjectFromKeyValuePair(pair, out var pairKey, out var subject) &&
                    pairKey is not null &&
                    PathSyntax.FormatIndex(pairKey) == text)
                {
                    matchedKey = pairKey;
                    return subject;
                }
            }
        }

        matchedKey = null;
        return null;
    }

    private static (KeyKind Kind, Type? KeyType) GetKeyKind(Type dictionaryType)
        => KeyKinds.GetOrAdd(dictionaryType, static type =>
        {
            var keyType = FindKeyType(type);
            if (keyType is null)
            {
                return (KeyKind.Unknown, null);
            }

            if (keyType.IsEnum)
            {
                return (KeyKind.Enum, keyType);
            }

            var kind = Type.GetTypeCode(keyType) switch
            {
                TypeCode.String => KeyKind.String,
                TypeCode.SByte => KeyKind.SByte,
                TypeCode.Byte => KeyKind.Byte,
                TypeCode.Int16 => KeyKind.Int16,
                TypeCode.UInt16 => KeyKind.UInt16,
                TypeCode.Int32 => KeyKind.Int32,
                TypeCode.UInt32 => KeyKind.UInt32,
                TypeCode.Int64 => KeyKind.Int64,
                TypeCode.UInt64 => KeyKind.UInt64,
                _ => keyType == typeof(Guid) ? KeyKind.Guid : KeyKind.Unknown
            };

            return (kind, keyType);
        });

    private static Type? FindKeyType(Type type)
    {
        if (IsGenericDictionary(type))
        {
            return type.GenericTypeArguments[0];
        }

        foreach (var implemented in type.GetInterfaces())
        {
            if (IsGenericDictionary(implemented))
            {
                return implemented.GenericTypeArguments[0];
            }
        }

        return null;
    }

    private static bool IsGenericDictionary(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>);
    }
}
