using System.Security.Cryptography;
using System.Text;

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

    private static string GetPathHash(string trimmedPath)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(trimmedPath), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }
}
