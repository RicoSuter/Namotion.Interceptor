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

    // The lock of a read or write is chosen by subject alone, so that no subject needs a lock of its own.
    private const int WriteStripeCount = 32;
    private static readonly Lock[] WriteLocks = CreateWriteLocks(WriteStripeCount);

    private readonly SecretStore _store;
    private readonly ILogger? _logger;

    // The secret properties that hold each key, by subject, weak so that a collected subject does not count. A property
    // lets go of a key when it moves to another key or its subject is released, and collected holders are dropped when
    // the key is used again. A list stays once created, so there are as many as keys were read or written. Each list is
    // locked while it is used.
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
    /// Sets the value for the JSON value of a secret property of <paramref name="owner"/> through <paramref name="set"/>:
    /// the stored value of a reference, null for a reference to a missing key, and any other value as it is. Only
    /// <see cref="Prefix"/> followed by 32 lowercase hexadecimal digits is a reference. The key of the property and its
    /// value change together for a concurrent <see cref="Write"/> of the same subject.
    /// </summary>
    public void Read<TState>(
        IInterceptorSubject owner, string propertyName, object? value, TState state, Action<IInterceptorSubject, TState, object?> set)
    {
        // The same lock as a write, so that a write never reads a value that is older than the key it stores it under.
        // The setter runs under the lock, so it must not take the lock of another subject.
        lock (WriteLocks[GetStripe(owner)])
        {
            set(owner, state, Resolve(owner, propertyName, value));
        }
    }

    // Remembers the key of a reference, or forgets it for any other value, and gives the value to set.
    private object? Resolve(IInterceptorSubject owner, string propertyName, object? value)
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
    /// remembered key of the property, or under a new key when there is none or another property of a live subject
    /// holds the key too, unless that key holds it already. A changed value is stored when it is written, before the
    /// file that references it, and concurrent writes of the same property leave the store with the value that the last
    /// of them read.
    /// </summary>
    public object? Write(IInterceptorSubject owner, string propertyName, Func<object, object?> get)
    {
        // The value is read under the lock, so that a write that waited for another one to store its value never replaces
        // it with an older value, and a property gets one key when its first writes overlap. Taken before the lock of the
        // store, never the other way round.
        lock (WriteLocks[GetStripe(owner)])
        {
            var text = (string?)get(owner);
            var remembered = owner.Data.TryGetValue((propertyName, RememberedKeyDataKey), out var data) ? data as RememberedKey : null;
            if (string.IsNullOrEmpty(text))
            {
                return text is null && remembered is { IsUnresolved: true } unresolved ? unresolved.Reference : text;
            }

            if (remembered is not null && _store.TryGet(remembered.Key, out var stored) && stored == text)
            {
                MarkResolved(owner, propertyName, remembered);
                return remembered.Reference;
            }

            // No key, or one that another property holds too, such as of a copied file and its original: the one that
            // changes its value gets a new key, and the other keeps the key.
            if (remembered is null || IsHeldByAnother(remembered.Key, owner, propertyName))
            {
                var key = SecretStore.CreateKey();
                var reference = Prefix + key;
                _store.Set(key, text);
                SetRemembered(owner, propertyName, new RememberedKey(key, reference, IsUnresolved: false));
                return reference;
            }

            _store.Set(remembered.Key, text);
            MarkResolved(owner, propertyName, remembered);
            return remembered.Reference;
        }
    }

    private static void MarkResolved(IInterceptorSubject owner, string propertyName, RememberedKey remembered)
    {
        if (remembered.IsUnresolved)
        {
            owner.Data[(propertyName, RememberedKeyDataKey)] = remembered with { IsUnresolved = false };
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

    // In the data of the subject, so it lives as long as the subject. Null forgets the key. Under the lock of the subject.
    private void SetRemembered(IInterceptorSubject owner, string propertyName, RememberedKey? remembered)
    {
        (string? Property, string Key) dataKey = (propertyName, RememberedKeyDataKey);
        object? previous;
        if (remembered is null)
        {
            owner.Data.TryRemove(dataKey, out previous);
        }
        else
        {
            Hold(remembered.Key, owner, propertyName);
            owner.Data.TryGetValue(dataKey, out previous);
            owner.Data[dataKey] = remembered;
        }

        if (previous is RememberedKey previousKey && previousKey.Key != remembered?.Key)
        {
            LetGo(previousKey.Key, owner, propertyName);
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
    private bool IsHeldByAnother(string key, IInterceptorSubject subject, string propertyName)
    {
        if (!_holders.TryGetValue(key, out var holders))
        {
            return false;
        }

        lock (holders)
        {
            foreach (var holder in holders)
            {
                if (holder.Subject.TryGetTarget(out var target) &&
                    (!ReferenceEquals(target, subject) || holder.PropertyName != propertyName))
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

    private static int GetStripe(IInterceptorSubject owner) =>
        (int)((uint)RuntimeHelpers.GetHashCode(owner) % WriteStripeCount);

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
