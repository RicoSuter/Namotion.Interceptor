using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage;

/// <summary>
/// The metadata of a subject stored as a JSON file, of a folder or of the root of a storage, which agents read and
/// change through its methods at <c>&lt;path&gt;/HomeBlaze:Metadata</c>. The storage puts it in the dynamic property
/// <see cref="IMetadataSubject.PropertyName"/> of its owner, also before a metadata file exists. It keeps no path:
/// each call finds the storage of its owner, which picks the file, and fails once the owner is no longer in it.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
public sealed partial class MetadataSubject : IMetadataSubject
{
    private const string GetMetadataDescription =
        "Gets every key of the metadata file of the subject, folder or storage root that this metadata belongs to, as a JSON object, keys HomeBlaze does not know included; {} without a file. For a folder or the root the order is left out: Get Order returns it.";

    private const string SetMetadataDescription =
        "Merges values, a JSON object, into the metadata file at the top level: a key with a value is set, replacing a nested value as a whole, a key with null is removed, and every other key stays. Keys are matched without regard to case. HomeBlaze reads title, navTitle and icon of a folder (the root uses none of them) and navTitle, icon, location (NavBar or AppBar) and alignment (Left or Right) of a subject; each takes a string, and a blank string removes it. The order of a folder or the root cannot be set here: use Set Order. Other keys are stored as given. At most 64 keys, key names of up to 64 characters, strings of up to 256 characters, values of up to 4 KB and a file of up to 16 KB, which a file that is larger already may exceed while the merge does not make it larger. Example: {\"navTitle\": \"Pump\", \"icon\": null}.";

    private const string GetOrderDescription =
        "Gets the names of the children of this folder or storage root in the order in which they are shown in navigation and in the browser. A name is the key of the child in Children. Only for a folder or the root.";

    private const string SetOrderDescription =
        "Sets the order in which the children of this folder or storage root are shown: the given names first, in the given order, then the other children in the order in which they are shown now. Each name must be the name of a child, as Get Order returns it, and may be given only once. Only for a folder or the root.";

    // Two storages give the same subject its metadata subject when a storage is the JSON file of another one.
    private static readonly Lock EnsureLock = new();

    private readonly IInterceptorSubject _owner;

    private MetadataSubject(IInterceptorSubject owner)
    {
        _owner = owner;
    }

    /// <inheritdoc />
    public partial SubjectMetadata? SubjectMetadata { get; internal set; }

    /// <summary>
    /// Gets every key of the metadata file of the owner as a JSON object, unknown keys included; an empty object
    /// without a file. For a folder or the root the order is left out.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The owner is no longer in a storage, the storage is not connected, or the metadata file could not be read.
    /// </exception>
    [Query(Title = "Get Metadata", Description = GetMetadataDescription, Position = 1)]
    [ExcludeFrom(ExclusionTarget.UserInterface)]
    public Task<JsonElement> GetMetadataAsync(CancellationToken cancellationToken)
        => RunAsync(() => ResolveStorage().GetMetadataAsync(_owner, cancellationToken));

    /// <inheritdoc />
    [Operation(Title = "Set Metadata", Description = SetMetadataDescription, Position = 2)]
    [ExcludeFrom(ExclusionTarget.UserInterface)]
    public Task SetMetadataAsync(JsonElement values, CancellationToken cancellationToken)
        => RunAsync(() => ResolveStorage().SetMetadataAsync(_owner, values, cancellationToken));

    /// <summary>
    /// Gets the names of the children of the folder or root in the order in which they are shown.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The owner is no folder or root, the storage is not connected, or the folder is no longer in it.
    /// </exception>
    [Query(Title = "Get Order", Description = GetOrderDescription, Position = 3)]
    [ExcludeFrom(ExclusionTarget.UserInterface)]
    public Task<string[]> GetOrderAsync(CancellationToken cancellationToken)
        => RunAsync(() => ResolveOrderStorage().GetChildOrderAsync(_owner, cancellationToken));

    /// <inheritdoc />
    [Operation(Title = "Set Order", Description = SetOrderDescription, Position = 4)]
    [ExcludeFrom(ExclusionTarget.UserInterface)]
    public Task SetOrderAsync(string[] names, CancellationToken cancellationToken)
        => RunAsync(() => ResolveOrderStorage().SetChildOrderAsync(_owner, names, cancellationToken));

    /// <summary>
    /// Gets the metadata subject of the owner, and gives it one when it has none. Null for an owner that is not
    /// registered, or whose property of that name holds something else.
    /// </summary>
    /// <remarks>Called on the worker of a storage, never from a lifecycle handler: the new subject is attached within the call.</remarks>
    internal static MetadataSubject? Ensure(IInterceptorSubject owner)
    {
        var registered = owner.TryGetRegisteredSubject();
        if (registered is null)
        {
            return null;
        }

        // The check and the add are one step, as a second AddProperty of the name would add a second property.
        lock (EnsureLock)
        {
            if (registered.TryGetProperty(IMetadataSubject.PropertyName) is { } property)
            {
                return property.GetValue() as MetadataSubject;
            }

            var metadataSubject = new MetadataSubject(owner);
            registered.AddProperty<MetadataSubject>(IMetadataSubject.PropertyName, _ => metadataSubject);
            return metadataSubject;
        }
    }

    // A refusal of the storage becomes an argument error, the only kind whose message McpMethodInvoker passes on to an
    // agent, so that the agent learns why: the file could not be read, the storage is not connected, or the owner is
    // no longer in it.
    private static async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> call)
    {
        try
        {
            return await call();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(exception.Message, exception);
        }
    }

    private static async Task RunAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(exception.Message, exception);
        }
    }

    // The storage that holds the metadata file of the owner: the storage of a folder, else the storage that holds the
    // owner as the subject of a file, else the owner itself as the root of a storage. A storage that is the JSON file
    // of another one so has the metadata of that file, and once it has left the tree, none.
    private FluentStorageContainer ResolveStorage()
        => (_owner is VirtualFolder ? AsStorage(_owner) : null)
            ?? FindHoldingStorage()
            ?? (_owner.TryGetRegisteredSubject() is not null ? _owner as FluentStorageContainer : null)
            ?? throw new ArgumentException("The subject of this metadata is no longer in a storage.");

    // The storage whose children are put in order: the storage of a folder, or the owner itself as the root.
    private FluentStorageContainer ResolveOrderStorage()
        => AsStorage(_owner)
            ?? throw new ArgumentException("Only a folder or the root of a storage has an order; this is the metadata of a subject.");

    // The folder or root whose children hold the owner, which makes the owner the subject of a file of that storage.
    private FluentStorageContainer? FindHoldingStorage()
    {
        var registered = _owner.TryGetRegisteredSubject();
        if (registered is null)
        {
            return null;
        }

        foreach (var parent in registered.Parents)
        {
            if (parent.Property.Name != IMetadataContainer.ChildrenPropertyName)
            {
                continue;
            }

            if (AsStorage(parent.Property.Parent.Subject) is { } storage)
            {
                return storage;
            }
        }

        return null;
    }

    // The storage of a folder, or the subject itself as the root of a storage.
    private static FluentStorageContainer? AsStorage(object? subject) => subject switch
    {
        VirtualFolder { Storage: FluentStorageContainer storage } => storage,
        FluentStorageContainer storage => storage,
        _ => null
    };
}
