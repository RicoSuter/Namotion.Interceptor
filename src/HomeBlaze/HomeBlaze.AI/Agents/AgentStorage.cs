using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The names of an agent's data files: a flat storage name derived from the agent's canonical path, followed by
/// one suffix per file.
/// </summary>
internal static class AgentStorage
{
    /// <summary>The suffix of the timeline file.</summary>
    public const string TimelineSuffix = ".timeline.jsonl";

    /// <summary>The suffix of the memory file.</summary>
    public const string MemorySuffix = ".memory.json";

    /// <summary>The suffix of the memory file of the earlier Markdown format.</summary>
    public const string LegacyMemorySuffix = ".memory.md";

    /// <summary>The suffix of the backend's session file.</summary>
    public const string SessionSuffix = ".session.json";

    /// <summary>The suffix of the run statistics file.</summary>
    public const string StatsSuffix = ".stats.json";

    /// <summary>The suffix of the working directory of external processes.</summary>
    public const string WorkingDirectorySuffix = ".workdir";

    private const string RootName = "_root";
    private const string HashSeparator = "__";
    private const char PathSeparator = '/';

    // The Windows set, so data directories move between platforms without renames.
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Every file and directory an agent keeps, including those its memory writes next to its own file.
    private static readonly string[] AllSuffixes =
    [
        TimelineSuffix,
        MemorySuffix,
        MemorySuffix + ".damaged",
        LegacyMemorySuffix,
        LegacyMemorySuffix + ".migrated",
        SessionSuffix,
        StatsSuffix,
        WorkingDirectorySuffix
    ];

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
    /// Moves the files of the agent at <paramref name="subjectPath"/> from the name of the earlier scheme, which
    /// mapped '/', '_' and invalid characters to '_' without a hash, to <see cref="GetName"/>. Each file moves only when
    /// no file of the new name with its suffix exists, so a migration that stopped halfway completes on the next start.
    /// Nothing moves when the names are equal, or when the earlier name is the current name of another path at which
    /// <paramref name="isSubjectAt"/> finds a subject, since the files then belong to that subject. Failures are
    /// logged, not thrown.
    /// </summary>
    /// <param name="directory">The directory of the agent data files.</param>
    /// <param name="subjectPath">The canonical path of the agent.</param>
    /// <param name="isSubjectAt">Whether a subject exists at a canonical path.</param>
    /// <param name="logger">Receives the moves and failures.</param>
    public static void MigrateLegacyName(string directory, string subjectPath, Func<string, bool> isSubjectAt, ILogger logger)
    {
        var name = GetName(subjectPath);
        var legacyName = GetLegacyName(subjectPath);
        if (name == legacyName)
        {
            return;
        }

        if (GetPathOfName(legacyName) is { } owner && isSubjectAt(owner))
        {
            logger.LogWarning(
                "The data files named {LegacyName} are not moved to the agent at {Path}: they are the files of the subject at {OwnerPath}.",
                legacyName, subjectPath, owner);
            return;
        }

        try
        {
            var moves = AllSuffixes
                .Select(suffix => (Source: Path.Combine(directory, legacyName + suffix), Target: Path.Combine(directory, name + suffix)))
                .Where(move => Exists(move.Source) && !Exists(move.Target))
                .ToList();
            if (moves.Count == 0)
            {
                return;
            }

            logger.LogInformation("Moving the data files of the agent at {Path} from {LegacyName} to {Name}.", subjectPath, legacyName, name);
            foreach (var (source, target) in moves)
            {
                Move(source, target, logger);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The data files of the agent at {Path} could not be moved to {Name}.", subjectPath, name);
        }
    }

    /// <summary>
    /// The path whose current name is <paramref name="name"/> when that name has no hash, else null. Such a name maps
    /// each '/' to '_' and has no other '_', so its path is the name with '_' read as '/'.
    /// </summary>
    private static string? GetPathOfName(string name)
    {
        var path = PathSeparator + name.Replace('_', PathSeparator);
        return GetName(path) == name ? path : null;
    }

    private static void Move(string source, string target, ILogger logger)
    {
        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The agent data file {Source} could not be moved to {Target}.", source, target);
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string GetLegacyName(string subjectPath)
    {
        var trimmed = subjectPath.Trim('/');
        if (trimmed.Length == 0)
        {
            return "root";
        }

        var characters = trimmed.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (InvalidFileNameCharacters.Contains(characters[index]) || char.IsControl(characters[index]))
            {
                characters[index] = '_';
            }
        }

        return new string(characters);
    }

    private static string GetPathHash(string trimmedPath)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(trimmedPath), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }
}
