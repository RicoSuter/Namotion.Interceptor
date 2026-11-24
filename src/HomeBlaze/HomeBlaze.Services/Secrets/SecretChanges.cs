namespace HomeBlaze.Services.Secrets;

/// <summary>
/// The secret values that serializations inside <see cref="ConfigurableSubjectSerializer.CaptureSecretChanges{TResult}"/>
/// wrote as references without storing them. The writer of the serialized configuration writes its file through
/// <see cref="StoreAroundAsync"/>. A write that is refused never calls it, which leaves the <see cref="SecretStore"/>
/// unchanged.
/// </summary>
/// <remarks>Not thread-safe: one writer uses it, for one write.</remarks>
public sealed class SecretChanges
{
    internal static readonly SecretChanges None = new(null);

    private readonly SecretReferences? _references;
    private List<SecretReferences.Change>? _changes;

    internal SecretChanges(SecretReferences? references)
    {
        _references = references;
    }

    internal List<SecretReferences.Change> Changes => _changes ??= [];

    internal SecretReferences.Change? Find(object owner, string propertyName)
    {
        if (_changes is not null)
        {
            foreach (var change in _changes)
            {
                if (ReferenceEquals(change.Owner, owner) && change.PropertyName == propertyName)
                {
                    return change;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Stores the values that got a new key, so that the file never references a missing key, then runs
    /// <paramref name="writeFile"/>, which writes the file that holds the references, and once it completed stores the
    /// changed values under the keys that the file referenced already and makes each subject keep the key it was written
    /// with. While <paramref name="writeFile"/> runs, a read of a changed key gives the changed value, as the file does
    /// once written. When <paramref name="writeFile"/> throws, no changed value is stored.
    /// </summary>
    /// <exception cref="IOException">
    /// The store cannot be written. Before <paramref name="writeFile"/> runs, it is not run. After it completed, the
    /// failure is logged, and the file references the previous value until a later save stores the changed one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The store cannot be written, as for an <see cref="IOException"/>.</exception>
    public Task StoreAroundAsync(Func<Task> writeFile) =>
        _changes is null ? writeFile() : _references!.StoreAroundAsync(_changes, writeFile);
}
