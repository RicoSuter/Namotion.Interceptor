using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    // A key is a GUID formatted with the "N" format of SecretStore.Add.
    private const int KeyLength = 32;
    private static readonly SearchValues<char> KeyCharacters = SearchValues.Create("0123456789abcdef");

    // Set by HasUnstoredSecrets for the serialization it runs, which is synchronous and runs on the calling thread.
    [ThreadStatic] private static bool t_isDryRun;
    [ThreadStatic] private static bool t_hasUnstored;

    // Set by Capture for the serialization it runs, as the dry run is.
    [ThreadStatic] private static SecretChanges? t_changes;

    private readonly SecretStore _store;
    private readonly ILogger? _logger;

    // The changed values of the files being written, by key. A page reads its own file while it writes it.
    private readonly ConcurrentDictionary<string, string> _pendingValues = new();

    // The first subject that read or wrote each key, weak so that a released subject neither stays alive nor keeps the key.
    private readonly ConcurrentDictionary<string, WeakReference<object>> _owners = new();

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
    /// reference to a missing key, and any other value as it is. Only <see cref="Prefix"/> followed by 32 lowercase
    /// hexadecimal digits is a reference.
    /// </summary>
    public object? Read(object owner, string propertyName, object? value)
    {
        if (value is not string text || !IsReference(text))
        {
            if (_keys.TryGetValue(owner, out var keys))
            {
                keys.TryRemove(propertyName, out _);
            }

            return value;
        }

        var key = text[Prefix.Length..];
        var isShared = !TryClaim(key, owner);
        if (TryGetValue(key, out var stored))
        {
            _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, text, IsUnresolved: false, isShared);
            return stored;
        }

        // Remembered, so the reference is written back while the property stays empty, and a restored store recovers it.
        _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, text, IsUnresolved: true, isShared);
        _logger?.LogWarning(
            "The secret {Key} of {Property} of {Type} is not in the secret store {Path}, so the property is empty. Enter it again, or restore the secret store and restart.",
            key, propertyName, owner.GetType().FullName, _store.Path);
        return null;
    }

    /// <summary>
    /// Gives the JSON value to write for the value of a secret property: a reference for a value other than null or
    /// empty. The value is stored under the remembered key of the property, or under a new key when there is none or
    /// another subject read the key first, unless that key holds it already. Inside <see cref="Capture{TResult}"/> it is
    /// recorded in the captured changes instead.
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

        var changes = t_isDryRun ? null : t_changes;
        if (changes?.Find(owner, propertyName) is { } captured)
        {
            // The same property serialized again for the same write.
            captured.Value = text;
            return captured.Reference;
        }

        if (remembered is { } known)
        {
            if (TryGetValue(known.Key, out var stored) && stored == text)
            {
                if (known.IsUnresolved)
                {
                    // Only when unchanged: a concurrent read may have remembered another key since.
                    keys!.TryUpdate(propertyName, known with { IsUnresolved = false }, known);
                }

                return known.Reference;
            }
        }

        if (t_isDryRun)
        {
            t_hasUnstored = true;
            return text;
        }

        if (remembered is { IsShared: false } owned)
        {
            if (changes is not null)
            {
                changes.Changes.Add(new Change(owner, propertyName, owned.Key, owned.Reference, IsNew: false) { Value = text });
            }
            else
            {
                _store.Set(owned.Key, text);
                if (owned.IsUnresolved)
                {
                    keys!.TryUpdate(propertyName, owned with { IsUnresolved = false }, owned);
                }
            }

            return owned.Reference;
        }

        // No key, or one that another subject read first, such as the original of a copied file, which keeps it.
        if (changes is not null)
        {
            var capturedKey = SecretStore.CreateKey();
            var capturedReference = Prefix + capturedKey;
            changes.Changes.Add(new Change(owner, propertyName, capturedKey, capturedReference, IsNew: true) { Value = text });
            return capturedReference;
        }

        var newKey = _store.Add(text);
        var reference = Prefix + newKey;
        Remember(owner, propertyName, newKey, reference);
        return reference;
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
            else
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

    private void Remember(object owner, string propertyName, string key, string reference) =>
        _keys.GetOrCreateValue(owner)[propertyName] = new RememberedKey(key, reference, IsUnresolved: false, IsShared: !TryClaim(key, owner));

    // Makes the owner the first subject of the key unless another one that is still alive is. A copied file shares the
    // key with its original, and only the first may replace its value. A released subject that was not collected yet
    // still counts, which costs another subject a new key on its next change.
    private bool TryClaim(string key, object owner)
    {
        while (true)
        {
            if (!_owners.TryGetValue(key, out var first))
            {
                if (_owners.TryAdd(key, new WeakReference<object>(owner)))
                {
                    return true;
                }
            }
            else if (first.TryGetTarget(out var target))
            {
                return ReferenceEquals(target, owner);
            }
            else if (_owners.TryUpdate(key, new WeakReference<object>(owner), first))
            {
                return true;
            }
        }
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

    private static bool IsReference(string text) =>
        text.Length == Prefix.Length + KeyLength &&
        text.StartsWith(Prefix, StringComparison.Ordinal) &&
        !text.AsSpan(Prefix.Length).ContainsAnyExcept(KeyCharacters);

    // Reference is the key with the prefix, kept so that writing an unchanged secret allocates no string.
    private readonly record struct RememberedKey(string Key, string Reference, bool IsUnresolved, bool IsShared);

    /// <summary>A secret value that a captured serialization wrote as a reference without storing it.</summary>
    internal sealed record Change(object Owner, string PropertyName, string Key, string Reference, bool IsNew)
    {
        public required string Value { get; set; }
    }
}
