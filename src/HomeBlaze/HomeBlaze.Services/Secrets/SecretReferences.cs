using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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

    // Set by Capture for the serialization it runs, which is synchronous and runs on the calling thread.
    [ThreadStatic] private static SecretChanges? t_changes;

    private readonly SecretStore _store;
    private readonly ILogger? _logger;

    // The changed values of the files being written, by key. A page reads its own file while it writes it.
    private readonly ConcurrentDictionary<string, string> _pendingValues = new();

    // The subjects that hold each key, weak so that a collected subject does not count. A subject lets go of a key when it
    // moves to another key or is released, and collected holders are dropped when the key is used again. A list stays
    // once created, so there are as many as keys were read or written. Each list is locked while it is used.
    private readonly ConcurrentDictionary<string, List<WeakReference<IInterceptorSubject>>> _holders = new();

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
        if (TryGetValue(key, out var stored))
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
    /// Gives the JSON value to write for the value of a secret property: a reference for a value other than null or
    /// empty. The value is stored under the remembered key of the property, or under a new key when there is none or
    /// another live subject holds the key too, unless that key holds it already. Inside <see cref="Capture{TResult}"/> it is
    /// recorded in the captured changes instead.
    /// </summary>
    /// <exception cref="IOException">The store cannot be written.</exception>
    public object? Write(IInterceptorSubject owner, string propertyName, object? value)
    {
        var text = (string?)value;
        (string? Property, string Key) dataKey = (propertyName, RememberedKeyDataKey);
        var remembered = owner.Data.TryGetValue(dataKey, out var data) ? data as RememberedKey : null;

        if (string.IsNullOrEmpty(text))
        {
            return text is null && remembered is { IsUnresolved: true } unresolved ? unresolved.Reference : text;
        }

        var changes = t_changes;
        if (changes?.Find(owner, propertyName) is { } captured)
        {
            // The same property serialized again for the same write.
            captured.Value = text;
            return captured.Reference;
        }

        if (remembered is { } known && TryGetValue(known.Key, out var stored) && stored == text)
        {
            MarkResolved(owner, dataKey, known);
            return known.Reference;
        }

        // No key, or one that another subject holds too, such as a copied file and its original: the one that changes its
        // value gets a new key, and the other keeps the key.
        var isNew = remembered is null || IsHeldByAnother(remembered.Key, owner);
        var key = isNew ? SecretStore.CreateKey() : remembered!.Key;
        var reference = isNew ? Prefix + key : remembered!.Reference;
        if (changes is not null)
        {
            changes.Changes.Add(new Change(owner, propertyName, key, reference, isNew) { Value = text });
            return reference;
        }

        _store.Set(key, text);
        if (isNew)
        {
            Remember(owner, propertyName, key, reference);
        }
        else
        {
            MarkResolved(owner, dataKey, remembered!);
        }

        return reference;
    }

    // Only when unchanged: a concurrent read may have remembered another key since.
    private static void MarkResolved(IInterceptorSubject owner, (string?, string) dataKey, RememberedKey remembered)
    {
        if (remembered.IsUnresolved)
        {
            owner.Data.TryUpdate(dataKey, remembered with { IsUnresolved = false }, remembered);
        }
    }

    /// <summary>
    /// Runs <paramref name="serialize"/> and records the secret values it would store in <paramref name="changes"/>
    /// instead of storing them.
    /// </summary>
    public TResult Capture<TResult>(Func<TResult> serialize, out SecretChanges changes)
    {
        // Restored afterwards, so that a nested capture does not end the one around it.
        var previous = t_changes;
        var current = new SecretChanges(this);
        t_changes = current;
        try
        {
            var result = serialize();
            changes = current;
            return result;
        }
        finally
        {
            t_changes = previous;
        }
    }

    /// <summary>Ends the capture of the calling thread until <see cref="ResumeCapture"/>, so that values are stored at once.</summary>
    public static SecretChanges? SuspendCapture()
    {
        var changes = t_changes;
        t_changes = null;
        return changes;
    }

    /// <summary>Resumes the capture that <see cref="SuspendCapture"/> ended.</summary>
    public static void ResumeCapture(SecretChanges? changes) => t_changes = changes;

    internal async Task StoreAroundAsync(List<Change> changes, Func<Task> writeFile)
    {
        foreach (var change in changes)
        {
            if (change.IsNew)
            {
                _store.Set(change.Key, change.Value);
            }
        }

        // After the new values, whose store write can throw: the finally below removes these.
        foreach (var change in changes)
        {
            if (!change.IsNew)
            {
                _pendingValues[change.Key] = change.Value;
            }
        }

        try
        {
            await writeFile();

            // The new keys first: the file references them, whatever happens to the changed values.
            foreach (var change in changes)
            {
                if (change.IsNew)
                {
                    Remember(change);
                }
            }

            foreach (var change in changes)
            {
                if (!change.IsNew)
                {
                    StoreChangedValue(change);
                    Remember(change);
                }
            }
        }
        finally
        {
            foreach (var change in changes)
            {
                if (!change.IsNew)
                {
                    // Only its own value: a later write of the same key may have replaced it.
                    _pendingValues.TryRemove(KeyValuePair.Create(change.Key, change.Value));
                }
            }
        }
    }

    private void StoreChangedValue(Change change)
    {
        try
        {
            _store.Set(change.Key, change.Value);
        }
        catch (Exception exception)
        {
            _logger?.LogError(
                exception,
                "The changed secret {Key} of {Property} of {Type} could not be stored after its file was written, so the previous value stays in the secret store {Path} until it is saved again.",
                change.Key, change.PropertyName, change.Owner.GetType().FullName, _store.Path);
            throw;
        }
    }

    // A changed value whose file is being written, which the file holds once written, before the store does.
    private bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
        _pendingValues.TryGetValue(key, out value) || _store.TryGet(key, out value);

    private void Remember(Change change) => Remember(change.Owner, change.PropertyName, change.Key, change.Reference);

    private void Remember(IInterceptorSubject owner, string propertyName, string key, string reference) =>
        SetRemembered(owner, propertyName, new RememberedKey(key, reference, IsUnresolved: false));

    /// <summary>Lets go of the keys that the secret properties of the subject hold, for a subject that is released.</summary>
    public void Release(IInterceptorSubject subject)
    {
        foreach (var (dataKey, data) in subject.Data)
        {
            if (dataKey.key == RememberedKeyDataKey && data is RememberedKey remembered)
            {
                LetGo(remembered.Key, subject);
            }
        }
    }

    // In the data of the subject, so it lives as long as the subject. Null forgets the key.
    private void SetRemembered(IInterceptorSubject owner, string propertyName, RememberedKey? remembered)
    {
        if (remembered is not null)
        {
            Hold(remembered.Key, owner);
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
                        LetGo(previous.Key, owner);
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

    private void Hold(string key, IInterceptorSubject subject)
    {
        var holders = _holders.GetOrAdd(key, static _ => []);
        lock (holders)
        {
            if (IndexOf(holders, subject) < 0)
            {
                holders.Add(new WeakReference<IInterceptorSubject>(subject));
            }
        }
    }

    private void LetGo(string key, IInterceptorSubject subject)
    {
        if (_holders.TryGetValue(key, out var holders))
        {
            lock (holders)
            {
                if (IndexOf(holders, subject) is var index and >= 0)
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
                if (holder.TryGetTarget(out var target) && !ReferenceEquals(target, subject))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Under the lock of the list: drops the holders that were collected and gives the index of the subject, or -1.
    private static int IndexOf(List<WeakReference<IInterceptorSubject>> holders, IInterceptorSubject subject)
    {
        var index = -1;
        var count = 0;
        for (var position = 0; position < holders.Count; position++)
        {
            if (holders[position].TryGetTarget(out var target))
            {
                index = ReferenceEquals(target, subject) ? count : index;
                holders[count++] = holders[position];
            }
        }

        holders.RemoveRange(count, holders.Count - count);
        return index;
    }

    private static bool IsReference(string text) =>
        text.Length == Prefix.Length + KeyLength &&
        text.StartsWith(Prefix, StringComparison.Ordinal) &&
        !text.AsSpan(Prefix.Length).ContainsAnyExcept(KeyCharacters);

    // Reference is the key with the prefix, kept so that writing an unchanged secret allocates no string.
    private sealed record RememberedKey(string Key, string Reference, bool IsUnresolved);

    /// <summary>A secret value that a captured serialization wrote as a reference without storing it.</summary>
    internal sealed record Change(IInterceptorSubject Owner, string PropertyName, string Key, string Reference, bool IsNew)
    {
        public required string Value { get; set; }
    }
}
