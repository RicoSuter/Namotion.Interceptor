using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HomeBlaze.Services.Secrets;

/// <summary>
/// Keeps the values of secret configuration properties in a JSON file outside the storage, each under a random key that
/// configuration files reference instead of the value. Entries are added or replaced, never removed.
/// </summary>
/// <remarks>
/// The file is read once, when the store is created, so a file that is replaced while the store lives takes effect
/// after a restart.
/// </remarks>
public sealed class SecretStore
{
    private readonly ConcurrentDictionary<string, string> _values;
    private readonly Lock _writeLock = new();

    /// <param name="path">The full path of the file.</param>
    /// <exception cref="InvalidOperationException">The file exists and is not a JSON object of strings.</exception>
    public SecretStore(string path)
    {
        Path = path;
        _values = Load(path);
    }

    /// <summary>The full path of the file.</summary>
    public string Path { get; }

    /// <summary>Gets the value stored under <paramref name="key"/>.</summary>
    public bool TryGet(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);

    /// <summary>Stores <paramref name="value"/> under a new key and returns the key once the file is written.</summary>
    /// <exception cref="IOException">The file cannot be written. The value is not stored.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written. The value is not stored.</exception>
    public string Add(string value)
    {
        var key = CreateKey();
        Set(key, value);
        return key;
    }

    /// <summary>Creates a new random key, 32 lowercase hexadecimal digits, without storing anything.</summary>
    internal static string CreateKey() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>, replacing its value or adding the key when it is
    /// missing, and returns once the file is written.
    /// </summary>
    /// <exception cref="IOException">The file cannot be written. The value is not stored.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written. The value is not stored.</exception>
    public void Set(string key, string value)
    {
        lock (_writeLock)
        {
            var values = new Dictionary<string, string>(_values) { [key] = value };
            var content = JsonSerializer.SerializeToUtf8Bytes(values, SecretStoreJsonContext.Default.DictionaryStringString);

            // Readable by the user HomeBlaze runs as only.
            AtomicFile.WriteAllBytes(Path, content, OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // After the write: a value that is not in the file must not be referenced.
            _values[key] = value;
        }
    }

    private static ConcurrentDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ConcurrentDictionary<string, string>();
        }

        try
        {
            var values = JsonSerializer.Deserialize(File.ReadAllBytes(path), SecretStoreJsonContext.Default.DictionaryStringString)
                ?? throw new JsonException("The file holds null.");

            foreach (var value in values.Values)
            {
                if (value is null)
                {
                    throw new JsonException("The file holds a null value.");
                }
            }

            return new ConcurrentDictionary<string, string>(values);
        }
        catch (JsonException exception)
        {
            // Never overwritten: the next add would replace every secret it holds.
            throw new InvalidOperationException(
                $"The secret store '{path}' is not a JSON object of strings. Correct or restore the file and start again.", exception);
        }
    }
}
