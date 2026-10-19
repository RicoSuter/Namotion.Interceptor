using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Applies the metadata file beside each subject that a storage holds as a JSON file, its stored subject, to that
/// subject and to the subjects and properties inside it.
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
    /// Gets whether the storage keeps the metadata of the subject: the storage itself, a folder of it, a stored subject
    /// of it, and a subject inside one. Thread-safe.
    /// </summary>
    public bool CanStoreMetadata(IInterceptorSubject subject)
    {
        if (ReferenceEquals(subject, _storage) || (subject is VirtualFolder folder && ReferenceEquals(folder.Storage, _storage)))
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

        // Published before the values are set: a subject that attaches meanwhile reads the file under the SyncRoot of
        // the subject, which setting its values takes as well, so either it reads this file or its values are set
        // after it.
        _files.AddOrUpdate(storedSubject, file);

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

    // Sets the metadata of the subject and of its properties from the file, and null where an entry is gone.
    private static void ApplyEntries(RegisteredSubject subject, RegisteredSubject stored, StorageMetadataFile file)
    {
        var isStored = ReferenceEquals(subject, stored);
        if (MetadataPathKey.GetKey(subject, stored) is not { } key)
        {
            return;
        }

        StorageMetadataValues.Set(subject, null, isStored ? file.Own : file.FindSubject(key));
        foreach (var property in subject.Properties)
        {
            if (!property.IsAttribute && property.Name != KnownAttributes.Metadata)
            {
                // The key of a property is built only when the file has entries of properties.
                var values = file.HasProperties ? file.FindProperty(MetadataPathKey.GetPropertyKey(key, property.Name)) : null;
                StorageMetadataValues.Set(subject, property.Name, values);
            }
        }
    }
}
