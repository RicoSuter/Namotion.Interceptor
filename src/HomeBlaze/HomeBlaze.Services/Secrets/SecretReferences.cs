using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Secrets;

/// <summary>
/// Turns the values of secret configuration properties into references to the <see cref="SecretStore"/> when they are
/// written, and references back into values when they are read. Remembers the key of each property of each subject, so
/// that an unchanged value keeps its key.
/// </summary>
internal sealed class SecretReferences
{
    /// <summary>The text that starts a reference, followed by the key.</summary>
    public const string Prefix = "$secret:";

    // Set by HasUnstoredSecrets for the serialization it runs, which is synchronous and runs on the calling thread.
    [ThreadStatic] private static bool t_isCheck;
    [ThreadStatic] private static bool t_hasUnstored;

    private readonly SecretStore _store;
    private readonly ILogger? _logger;

    // Weak, so that a released subject is not kept alive by the key of its secret.
    private readonly ConditionalWeakTable<object, ConcurrentDictionary<string, RememberedKey>> _keys = new();

    public SecretReferences(SecretStore store, ILogger? logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Whether the property is marked with <see cref="ConfigurationAttribute.IsSecret"/>.</summary>
    public static bool IsSecret(PropertyInfo property) =>
        property.GetCustomAttribute<ConfigurationAttribute>(true) is { IsSecret: true };

    /// <summary>Whether the registered property is marked with <see cref="ConfigurationAttribute.IsSecret"/>.</summary>
    public static bool IsSecret(RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ConfigurationAttribute { IsSecret: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the message of the exception for a secret property that is no <see cref="string"/> property of a subject, or
    /// null for a supported one.
    /// </summary>
    public static string? GetUnsupportedMessage(Type declaringType, string propertyName, Type propertyType) =>
        propertyType != typeof(string) || !typeof(IInterceptorSubject).IsAssignableFrom(declaringType)
            ? $"The secret configuration property '{propertyName}' of '{declaringType.FullName}' must be a string property of a subject."
            : null;

    /// <summary>Throws for a secret property that is no <see cref="string"/> property of a subject.</summary>
    /// <exception cref="InvalidOperationException">The property is not supported as a secret.</exception>
    public static void EnsureSupported(Type declaringType, string propertyName, Type propertyType)
    {
        if (GetUnsupportedMessage(declaringType, propertyName, propertyType) is { } message)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Gives the value to set for the JSON value of a secret property: the stored value of a reference, null for a
    /// reference to a missing key, and any other value as it is.
    /// </summary>
    public object? Read(object owner, string propertyName, object? value)
    {
        if (value is not string text || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (_keys.TryGetValue(owner, out var keys))
            {
                keys.TryRemove(propertyName, out _);
            }

            return value;
        }

        var key = text[Prefix.Length..];
        if (_store.TryGet(key, out var stored))
        {
            _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, IsUnresolved: false);
            return stored;
        }

        // Remembered, so the reference is written back while the property stays empty, and a restored store recovers it.
        _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, IsUnresolved: true);
        _logger?.LogWarning(
            "The secret {Key} of {Property} of {Type} is not in the secret store {Path}, so the property is empty. Enter it again, or restore the secret store and restart.",
            key, propertyName, owner.GetType().FullName, _store.Path);
        return null;
    }

    /// <summary>
    /// Gives the JSON value to write for the value of a secret property: a reference for a value other than null or
    /// empty, which is stored under a new key unless its remembered key holds it already.
    /// </summary>
    /// <exception cref="IOException">The store cannot be written.</exception>
    public object? Write(object owner, string propertyName, object? value)
    {
        var text = (string?)value;
        var remembered = _keys.TryGetValue(owner, out var keys) && keys.TryGetValue(propertyName, out var key) ? key : (RememberedKey?)null;

        if (string.IsNullOrEmpty(text))
        {
            return text is null && remembered is { IsUnresolved: true } unresolved ? Prefix + unresolved.Key : text;
        }

        if (remembered is { IsUnresolved: false } resolved && _store.TryGet(resolved.Key, out var stored) && stored == text)
        {
            return Prefix + resolved.Key;
        }

        if (t_isCheck)
        {
            t_hasUnstored = true;
            return text;
        }

        var newKey = _store.Add(text);
        _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(newKey, IsUnresolved: false);
        return Prefix + newKey;
    }

    /// <summary>
    /// Runs <paramref name="serialize"/> without storing anything and returns whether it found a secret value that has
    /// no stored key, such as one read as plaintext.
    /// </summary>
    public bool HasUnstoredSecrets(Action serialize)
    {
        t_isCheck = true;
        t_hasUnstored = false;
        try
        {
            serialize();
            return t_hasUnstored;
        }
        finally
        {
            t_isCheck = false;
            t_hasUnstored = false;
        }
    }

    private readonly record struct RememberedKey(string Key, bool IsUnresolved);
}
