using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The data files of an agent: their names, a flat storage name derived from the agent's canonical path followed by
/// one suffix per file, and their reading and writing.
/// </summary>
internal static class AgentStorage
{
    /// <summary>The suffix of the timeline file.</summary>
    public const string TimelineSuffix = ".timeline.jsonl";

    /// <summary>The suffix of the memory file.</summary>
    public const string MemorySuffix = ".memory.json";

    /// <summary>The suffix of the backend's session file.</summary>
    public const string SessionSuffix = ".session.json";

    /// <summary>The suffix of the run statistics file.</summary>
    public const string StatsSuffix = ".stats.json";

    /// <summary>The suffix of the working directory of external processes.</summary>
    public const string WorkingDirectorySuffix = ".workdir";

    private const string RootName = "_root";
    private const string HashSeparator = "__";

    // The Windows set, so data directories move between platforms without renames.
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// The storage name of the agent at a canonical path: its segments joined with '_', for example
    /// <c>Agents_Claude</c> for <c>/Agents/Claude</c>. When a segment contains '_' or a character that is invalid in
    /// file names, which is replaced by '_', the name ends with "__" and a hash of the path, so two paths never share
    /// a name. Paths that differ only in letter case get names that differ only in letter case, which share files on
    /// a case-insensitive file system.
    /// </summary>
    public static string GetName(string subjectPath)
    {
        var trimmed = subjectPath.Trim('/');
        if (trimmed.Length == 0)
        {
            // A segment never starts with '_' without getting a hash, so no path with segments has this name.
            return RootName;
        }

        var isAmbiguous = false;
        var characters = trimmed.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (character == '/')
            {
                // An empty segment would put "__" into the name, which marks a hash.
                isAmbiguous |= characters[index - 1] == '_';
                characters[index] = '_';
            }
            else if (character == '_' || InvalidFileNameCharacters.Contains(character) || char.IsControl(character))
            {
                isAmbiguous = true;
                characters[index] = '_';
            }
        }

        var name = new string(characters);
        return isAmbiguous ? name + HashSeparator + GetPathHash(trimmed) : name;
    }

    /// <summary>
    /// Reads a data file of an agent and parses it, or returns null when the agent has no storage, the file does not
    /// exist, or it cannot be read or parsed; read and parse failures are logged.
    /// </summary>
    /// <param name="path">The path of the file, null when the agent has no storage.</param>
    /// <param name="parse">Parses the content; may throw <see cref="JsonException"/>.</param>
    /// <param name="consequence">What happens when the file cannot be read, for the warning.</param>
    /// <param name="logger">The logger of the agent.</param>
    /// <param name="agent">The agent, named in the warning.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task<T?> TryReadJsonDataFileAsync<T>(
        string? path, Func<string, T?> parse, string consequence, ILogger logger, ITitleProvider agent, CancellationToken cancellationToken)
        where T : class
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "The file {Path} of agent {Agent} could not be read; {Consequence}.", path, agent.Title, consequence);
            return null;
        }
    }

    /// <summary>
    /// Writes a data file of an agent atomically, or deletes it for null content; does nothing when the agent has no
    /// storage. Write failures are logged, not thrown.
    /// </summary>
    /// <param name="path">The path of the file, null when the agent has no storage.</param>
    /// <param name="content">The content, or null to delete the file.</param>
    /// <param name="consequence">What happens when the file cannot be written, for the warning.</param>
    /// <param name="logger">The logger of the agent.</param>
    /// <param name="agent">The agent, named in the warning.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task TryWriteDataFileAsync(
        string? path, string? content, string consequence, ILogger logger, ITitleProvider agent, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (content is null)
            {
                File.Delete(path);
                return;
            }

            await AtomicFile.WriteAllTextAsync(path, content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The file {Path} of agent {Agent} could not be written; {Consequence}.", path, agent.Title, consequence);
        }
    }

    private static string GetPathHash(string trimmedPath)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(trimmedPath), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }
}
