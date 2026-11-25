using System.Buffers;
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

    // A key is the format of SecretStore.CreateKey.
    private const int KeyLength = 32;
    private static readonly SearchValues<char> KeyCharacters = SearchValues.Create("0123456789abcdef");

    // The key of the remembered key of a secret property in the data of its subject.
    private const string RememberedKeyDataKey = "HomeBlaze.Secrets.Key";

    // The lock of a changed write is chosen by subject and property, so that no subject needs a lock of its own.
    private static readonly Lock[] WriteLocks = CreateWriteLocks(32);

    private readonly SecretStore _store;
    private readonly ILogger? _logger;

    // The secret properties that hold each key, by subject, weak so that a collected subject does not count. A property
    // lets go of a key when it moves to another key or its subject is released, and collected holders are dropped when the key is used again. A list stays
    // once created, so there are as many as keys were read or written. Each list is locked while it is used.
    private readonly ConcurrentDictionary<string, List<Holder>> _holders = new();

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
    /// reference to a missing key, and any other value as it is. Only <see cref="Prefix"/> followed by 32 lowercase
    /// hexadecimal digits is a reference.
    /// </summary>
    public object? Read(IInterceptorSubject owner, string propertyName, object? value)
    {
        if (value is not string text || !IsReference(text))
        {
            SetRemembered(owner, propertyName, null);
            return value;
        }

        var key = text[Prefix.Length..];
        if (_store.TryGet(key, out var stored))
        {
            SetRemembered(owner, propertyName, new RememberedKey(key, text, IsUnresolved: false));
            return stored;
        }

        // Remembered, so the reference is written back while the property stays empty, and a restored store recovers it.
        SetRemembered(owner, propertyName, new RememberedKey(key, text, IsUnresolved: true));
        _logger?.LogWarning(
            "The secret {Key} of {Property} of {Type} is not in the secret store {Path}, so the property is empty. Enter it again, or restore the secret store and restart.",
            key, propertyName, owner.GetType().FullName, _store.Path);
        return null;
    }

    /// <summary>
    /// Gives the JSON value to write for the value of a secret property, which <paramref name="get"/> reads from
    /// <paramref name="owner"/>: a reference for a value other than null or empty. The value is stored under the
    /// remembered key of the property, or under a new key when there is none or another live subject holds the key too,
    /// unless that key holds it already. A changed value is stored when it is written, before the file that references
    /// it, and concurrent writes of the same property leave the store with the value that the last of them read.
    /// </summary>
    /// <exception cref="IOException">The store cannot be written.</exception>
    public object? Write(IInterceptorSubject owner, string propertyName, Func<object, object?> get)
    {
        (string? Property, string Key) dataKey = (propertyName, RememberedKeyDataKey);
        if (TryWriteUnchanged(owner, dataKey, (string?)get(owner), out var result, out _))
        {
            return result;
        }

        // The value is read again under the lock, so that a write that waited for another one to store its value never
        // replaces it with the value it read before, and a property gets one key when its first writes overlap. Taken
        // before the lock of the store, never the other way round.
        lock (WriteLocks[(int)((uint)HashCode.Combine(RuntimeHelpers.GetHashCode(owner), propertyName) % WriteLocks.Length)])
        {
            var text = (string?)get(owner);
            if (TryWriteUnchanged(owner, dataKey, text, out result, out var remembered))
            {
                return result;
            }

            // No key, or one that another subject holds too, such as a copied file and its original: the one that changes
            // its value gets a new key, and the other keeps the key.
            var isNew = remembered is null || IsHeldByAnother(remembered.Key, owner);
            var key = isNew ? SecretStore.CreateKey() : remembered!.Key;
            var reference = isNew ? Prefix + key : remembered!.Reference;
            _store.Set(key, text!);
            if (isNew)
            {
                SetRemembered(owner, propertyName, new RememberedKey(key, reference, IsUnresolved: false));
            }
            else
            {
                MarkResolved(owner, dataKey, remembered!);
            }

            return reference;
        }
    }

    // Gives the JSON value for null or empty, or for a value that the remembered key holds already.
    private bool TryWriteUnchanged(
        IInterceptorSubject owner, (string?, string) dataKey, string? text, out object? result, out RememberedKey? remembered)
    {
        remembered = owner.Data.TryGetValue(dataKey, out var data) ? data as RememberedKey : null;
        if (string.IsNullOrEmpty(text))
        {
            result = text is null && remembered is { IsUnresolved: true } unresolved ? unresolved.Reference : text;
            return true;
        }

        if (remembered is { } known && _store.TryGet(known.Key, out var stored) && stored == text)
        {
            MarkResolved(owner, dataKey, known);
            result = known.Reference;
            return true;
        }

        result = null;
        return false;
    }

    // Only when unchanged: a concurrent read may have remembered another key since.
    private static void MarkResolved(IInterceptorSubject owner, (string?, string) dataKey, RememberedKey remembered)
    {
        if (remembered.IsUnresolved)
        {
            owner.Data.TryUpdate(dataKey, remembered with { IsUnresolved = false }, remembered);
        }
    }

    /// <summary>Lets go of the keys that the secret properties of the subject hold, for a subject that is released.</summary>
    public void Release(IInterceptorSubject subject)
    {
        foreach (var (dataKey, data) in subject.Data)
        {
            if (dataKey.key == RememberedKeyDataKey && data is RememberedKey remembered)
            {
                LetGo(remembered.Key, subject, dataKey.property!);
            }
        }
    }

    // In the data of the subject, so it lives as long as the subject. Null forgets the key.
    private void SetRemembered(IInterceptorSubject owner, string propertyName, RememberedKey? remembered)
    {
        if (remembered is not null)
        {
            Hold(remembered.Key, owner, propertyName);
        }

        (string? Property, string Key) dataKey = (propertyName, RememberedKeyDataKey);
        while (true)
        {
            if (owner.Data.TryGetValue(dataKey, out var data))
            {
                var isReplaced = remembered is null
                    ? owner.Data.TryRemove(KeyValuePair.Create(dataKey, data))
                    : owner.Data.TryUpdate(dataKey, remembered, data);
                if (isReplaced)
                {
                    if (data is RememberedKey previous && previous.Key != remembered?.Key)
                    {
                        LetGo(previous.Key, owner, propertyName);
                    }

                    return;
                }
            }
            else if (remembered is null || owner.Data.TryAdd(dataKey, remembered))
            {
                return;
            }
        }
    }

    private void Hold(string key, IInterceptorSubject subject, string propertyName)
    {
        var holders = _holders.GetOrAdd(key, static _ => []);
        lock (holders)
        {
            if (IndexOf(holders, subject, propertyName) < 0)
            {
                holders.Add(new Holder(new WeakReference<IInterceptorSubject>(subject), propertyName));
            }
        }
    }

    private void LetGo(string key, IInterceptorSubject subject, string propertyName)
    {
        if (_holders.TryGetValue(key, out var holders))
        {
            lock (holders)
            {
                if (IndexOf(holders, subject, propertyName) is var index and >= 0)
                {
                    holders.RemoveAt(index);
                }
            }
        }
    }

    // A subject that is no longer used, but was neither released nor collected yet, still counts, which costs the other
    // subject a new key on its next change.
    private bool IsHeldByAnother(string key, IInterceptorSubject subject)
    {
        if (!_holders.TryGetValue(key, out var holders))
        {
            return false;
        }

        lock (holders)
        {
            foreach (var holder in holders)
            {
                if (holder.Subject.TryGetTarget(out var target) && !ReferenceEquals(target, subject))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Under the lock of the list: drops the holders that were collected and gives the index of the property, or -1.
    private static int IndexOf(List<Holder> holders, IInterceptorSubject subject, string propertyName)
    {
        var index = -1;
        var count = 0;
        for (var position = 0; position < holders.Count; position++)
        {
            if (holders[position].Subject.TryGetTarget(out var target))
            {
                index = ReferenceEquals(target, subject) && holders[position].PropertyName == propertyName ? count : index;
                holders[count++] = holders[position];
            }
        }

        holders.RemoveRange(count, holders.Count - count);
        return index;
    }

    private static Lock[] CreateWriteLocks(int count)
    {
        var locks = new Lock[count];
        for (var index = 0; index < count; index++)
        {
            locks[index] = new Lock();
        }

        return locks;
    }

    internal static bool IsReference(string text) =>
        text.Length == Prefix.Length + KeyLength &&
        text.StartsWith(Prefix, StringComparison.Ordinal) &&
        !text.AsSpan(Prefix.Length).ContainsAnyExcept(KeyCharacters);

    private readonly record struct Holder(WeakReference<IInterceptorSubject> Subject, string PropertyName);

    // Reference is the key with the prefix, kept so that writing an unchanged secret allocates no string.
    private sealed record RememberedKey(string Key, string Reference, bool IsUnresolved);
}
