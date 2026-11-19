namespace HomeBlaze.Installation;

/// <summary>
/// The version and commit of a build; an install succeeded when the identity of the running build differs from the
/// requested one, which for an edge build, whose version stays "edge", means a different commit.
/// </summary>
internal sealed record BuildIdentity(string Version, string? Commit)
{
    public string Describe()
    {
        if (!string.Equals(Version, "edge", StringComparison.OrdinalIgnoreCase) || Commit is not { } commit)
        {
            return Version;
        }

        return commit.Length > 7 ? commit[..7] : commit;
    }

    public string Format() => $"{Version}\n{Commit}\n";

    public static BuildIdentity Parse(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries);
        var commit = lines.Length > 1 && lines[1].Length > 0 ? lines[1] : null;
        return new BuildIdentity(lines[0], commit);
    }
}
