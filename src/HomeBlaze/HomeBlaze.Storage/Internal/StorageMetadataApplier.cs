using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Applies the metadata file beside each subject that a storage holds as a JSON file, its stored subject, to that
/// subject and to the subjects and properties inside it: the whole file when it is loaded or written, and the entries
/// of a subject or property that attaches later.
/// </summary>
internal sealed class StorageMetadataApplier
{
    // Guards against a cycle of parents, which a walk up would follow forever.
    private const int MaxDepth = 256;

    private readonly FluentStorageContainer _storage;

    // The file applied last to each stored subject. Weak, so that a subject that has left the storage is not kept; a
    // file holds no subject.
    private readonly ConditionalWeakTable<IInterceptorSubject, StorageMetadataFile> _files = new();

    public StorageMetadataApplier(FluentStorageContainer storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Gets whether the storage keeps the metadata of the subject: the storage itself unless another storage holds it,
    /// a folder of it, a stored subject of it, and a subject inside one. Thread-safe.
    /// </summary>
    public bool CanStoreMetadata(IInterceptorSubject subject)
    {
        if (ReferenceEquals(subject, _storage))
        {
            return ReferenceEquals(subject.FindMetadataStore(), _storage);
        }

        if (subject is VirtualFolder folder && ReferenceEquals(folder.Storage, _storage))
        {
            return true;
        }

        return subject.TryGetRegisteredSubject() is { } registered &&
               TryFindStoredSubject(registered, out var storedSubject) &&
               _files.TryGetValue(storedSubject.Subject, out _);
    }

    /// <summary>
    /// Finds the stored subject of this storage that holds the subject, the subject itself included: the one held by
    /// the <see cref="IMetadataContainer.ChildrenPropertyName"/> of the storage or of a folder of it, reached through
    /// the first parent of each subject on the way up. Folders are found as well, as they are held the same way.
    /// </summary>
    public bool TryFindStoredSubject(RegisteredSubject subject, [NotNullWhen(true)] out RegisteredSubject? storedSubject)
    {
        var current = subject;
        for (var depth = 0; depth < MaxDepth && current.Parents is [var first, ..]; depth++)
        {
            var parent = first.Property;
            if (parent.Name == IMetadataContainer.ChildrenPropertyName && parent.Subject is IMetadataContainer container)
            {
                var isOwn = container is VirtualFolder folder ? ReferenceEquals(folder.Storage, _storage) : ReferenceEquals(container, _storage);
                storedSubject = isOwn ? current : null;
                return isOwn;
            }

            current = parent.Parent;
        }

        storedSubject = null;
        return false;
    }

    /// <summary>
    /// Applies a file to the stored subject beside it and to everything inside that subject: sets every subject and
    /// property that has an entry, and null where one has metadata without an entry. Called on the worker.
    /// </summary>
    public void Apply(IInterceptorSubject storedSubject, StorageMetadataFile file)
    {
        if (storedSubject.TryGetRegisteredSubject() is not { } stored)
        {
            return;
        }

        // Without entries in this file and in the one applied before, nothing inside the stored subject has metadata.
        var hasEntries = HasEntries(file) || (_files.TryGetValue(storedSubject, out var previous) && HasEntries(previous));

        // Published before the values are set: a subject that attaches meanwhile reads the file under the SyncRoot of
        // the subject, which setting its values takes as well, so either it reads this file or its values are set
        // after it.
        _files.AddOrUpdate(storedSubject, file);
        if (!hasEntries)
        {
            StorageMetadataValues.Set(stored, null, file.Own);
            return;
        }

        var visited = new HashSet<RegisteredSubject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<RegisteredSubject>();
        pending.Push(stored);
        while (pending.TryPop(out var subject))
        {
            if (!visited.Add(subject))
            {
                continue;
            }

            ApplyEntries(subject, stored, file);
            foreach (var property in subject.Properties)
            {
                // The children of a storage that this one holds are its own stored subjects.
                if (!property.CanContainSubjects || property.IsAttribute ||
                    (property.Name == IMetadataContainer.ChildrenPropertyName && property.Subject is IMetadataContainer))
                {
                    continue;
                }

                foreach (var child in property.Children)
                {
                    // A subject that this property only references is applied where its first parent holds it.
                    if (child.Subject.TryGetRegisteredSubject() is { Parents: [var parent, ..] } registeredChild && parent.Property == property)
                    {
                        pending.Push(registeredChild);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Applies the entries of a subject that attached and of its properties, or with <paramref name="propertyName"/>
    /// the entry of a property that was added to an attached subject, so that a subject that is created or replaced
    /// inside a stored subject, and a property that is added at run time, take their metadata. Thread-safe.
    /// </summary>
    public void ApplyStoredMetadata(IInterceptorSubject attachedSubject, string? propertyName)
    {
        // A subject that attaches is applied with a file without entries as well, as it may bring metadata from where
        // it was before; a property that is added has none, so it needs an entry. Metadata stays stale when the subject
        // moves outside any storage or into a stored subject without an applied file, and when the reference to its
        // new place is added before the old one is removed, as the subject then does not attach again.
        if (attachedSubject.TryGetRegisteredSubject() is not { } subject ||
            !TryFindStoredSubject(subject, out var stored) ||
            !_files.TryGetValue(stored.Subject, out var file) ||
            (propertyName is not null && !file.HasProperties))
        {
            return;
        }

        lock (attachedSubject.SyncRoot)
        {
            // Read again under the lock, see Apply.
            if (!_files.TryGetValue(stored.Subject, out file))
            {
                return;
            }

            if (propertyName is null)
            {
                ApplyEntries(subject, stored, file);
            }
            else if (file.HasProperties && MetadataPathKey.GetKey(subject, stored) is { } key)
            {
                StorageMetadataValues.Set(subject, propertyName, file.FindProperty(MetadataPathKey.GetPropertyKey(key, propertyName)));
            }
        }
    }

    // Sets the metadata of the subject and of its properties from the file, and null where an entry is gone.
    private static void ApplyEntries(RegisteredSubject subject, RegisteredSubject stored, StorageMetadataFile file)
    {
        // Without entries, only the stored subject has keys, its own, so the path is not needed.
        string? key = null;
        if (HasEntries(file) && (key = MetadataPathKey.GetKey(subject, stored)) is null)
        {
            return;
        }

        var values = ReferenceEquals(subject, stored) ? file.Own : key is null ? null : file.FindSubject(key);
        if (values is not null || subject.TryGetProperty(KnownAttributes.Metadata) is not null)
        {
            StorageMetadataValues.Set(subject, null, values);
        }

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || property.Name == KnownAttributes.Metadata)
            {
                continue;
            }

            values = key is not null && file.HasProperties ? file.FindProperty(MetadataPathKey.GetPropertyKey(key, property.Name)) : null;
            if (values is not null || property.TryGetAttribute(KnownAttributes.Metadata) is not null)
            {
                StorageMetadataValues.Set(subject, property.Name, values);
            }
        }
    }

    private static bool HasEntries(StorageMetadataFile file) => file.HasSubjects || file.HasProperties;
}
