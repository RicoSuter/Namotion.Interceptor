using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Components.Metadata;

/// <summary>Finds where the metadata of a subject is edited.</summary>
public static class MetadataEditing
{
    /// <summary>
    /// Gets the store that keeps the metadata of the subject, or null when none does. A store keeps its own metadata only
    /// when its parent is kept by a store, as a storage at the root has no file to keep it in.
    /// </summary>
    public static IMetadataStore? FindStore(IInterceptorSubject subject)
        => subject.FindMetadataStore() is { } store && store.CanStoreMetadata(subject) &&
           (!ReferenceEquals(store, subject) || subject.TryGetRegisteredSubject() is { Parents: [var parent, ..] } && parent.Property.Parent.Subject.FindMetadataStore() is not null)
            ? store
            : null;

    /// <summary>
    /// Gets whether the container can be written now: anything but a storage that is not connected. A folder reports the
    /// status of its storage.
    /// </summary>
    public static bool IsConnected(object container)
        => container is not IStorageContainer { Status: not StorageStatus.Connected };
}
