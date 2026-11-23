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
/// that a secret keeps its key when its value changes.
/// </summary>
internal sealed class SecretReferences
{
    /// <summary>The text that starts a reference, followed by the key.</summary>
    public const string Prefix = "$secret:";

    // Set by HasUnstoredSecrets for the serialization it runs, which is synchronous and runs on the calling thread.
    [ThreadStatic] private static bool t_isDryRun;
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
    /// Gets the message of the exception for a secret property that is not a <see cref="string"/> property of a subject, or
    /// null for a supported one.
    /// </summary>
    public static string? GetUnsupportedMessage(Type declaringType, string propertyName, Type propertyType) =>
        propertyType != typeof(string) || !typeof(IInterceptorSubject).IsAssignableFrom(declaringType)
            ? $"The secret configuration property '{propertyName}' of '{declaringType.FullName}' must be a string property of a subject."
            : null;

    /// <summary>Throws for a secret property that is not a <see cref="string"/> property of a subject.</summary>
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
            _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, text, IsUnresolved: false);
            return stored;
        }

        // Remembered, so the reference is written back while the property stays empty, and a restored store recovers it.
        _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, text, IsUnresolved: true);
        _logger?.LogWarning(
            "The secret {Key} of {Property} of {Type} is not in the secret store {Path}, so the property is empty. Enter it again, or restore the secret store and restart.",
            key, propertyName, owner.GetType().FullName, _store.Path);
        return null;
    }

    /// <summary>
    /// Gives the JSON value to write for the value of a secret property: a reference for a value other than null or
    /// empty. The value is stored under the remembered key of the property, or under a new key when there is none, unless
    /// that key holds it already.
    /// </summary>
    /// <exception cref="IOException">The store cannot be written.</exception>
    public object? Write(object owner, string propertyName, object? value)
    {
        var text = (string?)value;
        var remembered = _keys.TryGetValue(owner, out var keys) && keys.TryGetValue(propertyName, out var key) ? key : (RememberedKey?)null;

        if (string.IsNullOrEmpty(text))
        {
            return text is null && remembered is { IsUnresolved: true } unresolved ? unresolved.Reference : text;
        }

        if (remembered is { } known)
        {
            if (!_store.TryGet(known.Key, out var stored) || stored != text)
            {
                if (t_isDryRun)
                {
                    t_hasUnstored = true;
                    return text;
                }

                _store.Set(known.Key, text);
            }

            if (known.IsUnresolved)
            {
                // Only when unchanged: a concurrent read may have remembered another key since.
                keys!.TryUpdate(propertyName, known with { IsUnresolved = false }, known);
            }

            return known.Reference;
        }

        if (t_isDryRun)
        {
            t_hasUnstored = true;
            return text;
        }

        var newKey = _store.Add(text);
        var reference = Prefix + newKey;
        _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(newKey, reference, IsUnresolved: false);
        return reference;
    }

    /// <summary>
    /// Runs <paramref name="serialize"/> without storing anything and returns whether it found a secret value that its
    /// key does not hold yet, such as one read as plaintext or a changed one.
    /// </summary>
    public bool HasUnstoredSecrets(Action serialize)
    {
        // Restored afterwards, so that a nested dry run does not end the one around it.
        var wasDryRun = t_isDryRun;
        var hadUnstored = t_hasUnstored;
        t_isDryRun = true;
        t_hasUnstored = false;
        try
        {
            serialize();
            return t_hasUnstored;
        }
        finally
        {
            t_isDryRun = wasDryRun;
            t_hasUnstored = hadUnstored;
        }
    }

    // Reference is the key with the prefix, kept so that writing an unchanged secret allocates no string.
    private readonly record struct RememberedKey(string Key, string Reference, bool IsUnresolved);
}
