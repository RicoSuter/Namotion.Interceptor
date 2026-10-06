using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

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

        var shape = GetShape(property.Type);
        if (shape.IsCollection)
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

        if (shape.IsDictionary)
        {
            var dictionary = property.GetValue();
            var keyShape = dictionary is null || dictionary.GetType() == property.Type ? shape : GetShape(dictionary.GetType());
            switch (TryParseKey(keyShape, text, textString, out var parsedKey))
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
        position = 0;
        return IsCanonicalInteger(text, allowSign: false) &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out position);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is an integer as the invariant culture writes it: digits without a leading zero
    /// (other than "0" itself), preceded by '-' for a negative number when <paramref name="allowSign"/>.
    /// </summary>
    private static bool IsCanonicalInteger(ReadOnlySpan<char> text, bool allowSign)
    {
        if (allowSign && text.Length > 1 && text[0] == '-')
        {
            text = text[1..];
            if (text[0] == '0')
            {
                return false;
            }
        }

        return text.Length > 0 &&
            (text[0] != '0' || text.Length == 1) &&
            !text.ContainsAnyExceptInRange('0', '9');
    }

    /// <returns>True when parsed, false when the key type is supported but the text is not its written form, null when the key type is not supported.</returns>
    private static bool? TryParseKey(Shape shape, ReadOnlySpan<char> text, string? textString, out object? key)
    {
        switch (shape.KeyKind)
        {
            case KeyKind.String: key = textString ?? text.ToString(); return true;
            case KeyKind.SByte: key = ParseInteger<sbyte>(text); break;
            case KeyKind.Byte: key = ParseInteger<byte>(text); break;
            case KeyKind.Int16: key = ParseInteger<short>(text); break;
            case KeyKind.UInt16: key = ParseInteger<ushort>(text); break;
            case KeyKind.Int32: key = ParseInteger<int>(text); break;
            case KeyKind.UInt32: key = ParseInteger<uint>(text); break;
            case KeyKind.Int64: key = ParseInteger<long>(text); break;
            case KeyKind.UInt64: key = ParseInteger<ulong>(text); break;

            case KeyKind.Enum:
                key = null;
                // The written-form check rejects numeric forms of named values and alias names, which the writer never emits.
                if (Enum.TryParse(shape.KeyType!, text, ignoreCase: false, out var enumValue) && PathSyntax.TextEquals((ISpanFormattable)enumValue!, text))
                {
                    key = enumValue;
                }

                break;

            case KeyKind.Guid:
                key = null;
                if (Guid.TryParse(text, out var guidValue) && PathSyntax.TextEquals(guidValue, text))
                {
                    key = guidValue;
                }

                break;

            default:
                key = null;
                return null;
        }

        return key is not null;
    }

    private static object? ParseInteger<T>(ReadOnlySpan<char> text) where T : struct, IBinaryInteger<T>
        => IsCanonicalInteger(text, allowSign: true) &&
           T.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
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

    private static Shape GetShape(Type type) => Shapes.GetOrAdd(type, static type => new Shape(type));

    private enum KeyKind : byte
    {
        Unsupported,
        String,
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Enum,
        Guid
    }

    /// <summary>The resolver's view of a property or dictionary type: container kind and how its keys parse.</summary>
    private sealed class Shape
    {
        public Shape(Type type)
        {
            IsCollection = type.IsSubjectCollectionType();
            IsDictionary = type.IsSubjectDictionaryType();
            KeyType = SubjectLookup.GetDictionaryKeyType(type);
            KeyKind = GetKeyKind(KeyType);
        }

        public bool IsCollection { get; }

        public bool IsDictionary { get; }

        public Type? KeyType { get; }

        public KeyKind KeyKind { get; }

        private static KeyKind GetKeyKind(Type? keyType)
        {
            if (keyType is null)
            {
                return KeyKind.Unsupported;
            }

            if (keyType.IsEnum)
            {
                return KeyKind.Enum;
            }

            if (keyType == typeof(Guid))
            {
                return KeyKind.Guid;
            }

            return Type.GetTypeCode(keyType) switch
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
                _ => KeyKind.Unsupported
            };
        }
    }
}
