namespace HomeBlaze.AI.Tests.Agents.Cli;

/// <summary>
/// Writes a shell script that stands in for the Claude CLI: it records each invocation's arguments, environment,
/// standard input and MCP configuration, then replays a fixture. Disposing deletes its temporary directory.
/// </summary>
internal sealed class FakeClaudeCli : IDisposable
{
    private FakeClaudeCli(string directory, string scriptPath)
    {
        Directory = directory;
        ScriptPath = scriptPath;
    }

    public string Directory { get; }

    public string ScriptPath { get; }

    /// <summary>
    /// Arguments of every invocation, in order. An argument containing a newline spans several entries.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Invocations
    {
        get
        {
            var path = Path.Combine(Directory, "arguments.txt");
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadAllText(path)
                .Split("---END---\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(block => (IReadOnlyList<string>)block.Split('\n').SkipLast(1).ToList())
                .ToList();
        }
    }

    public string LastStandardInput => File.ReadAllText(Path.Combine(Directory, "stdin.txt"));

    /// <summary>The environment of the last invocation as <c>NAME=value</c> lines.</summary>
    public IReadOnlyList<string> LastEnvironment => File.ReadAllLines(Path.Combine(Directory, "environment.txt"));

    public string LastMcpConfiguration => File.ReadAllText(Path.Combine(Directory, "mcp.json"));

    /// <summary>The octal permission bits of the last MCP configuration file, for example <c>600</c>.</summary>
    public string LastMcpConfigurationMode => File.ReadAllText(Path.Combine(Directory, "mcp-mode.txt")).Trim();

    public int? LastProcessId
    {
        get
        {
            var path = Path.Combine(Directory, "pid.txt");
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var processId) ? processId : null;
        }
    }

    /// <summary>Whether the process of the last invocation still runs.</summary>
    public bool IsLastProcessRunning
    {
        get
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(LastProcessId!.Value);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>The argument after <paramref name="flag"/>, or null when the flag is missing or last.</summary>
    public static string? ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary directory left behind does not affect the test outcome.
        }
    }

    /// <summary>
    /// Creates the script.
    /// </summary>
    /// <param name="fixture">Fixture file name replayed on standard output.</param>
    /// <param name="resumeFixture">Fixture replayed instead when the arguments contain --resume, with exit code 1 and the missing-session message.</param>
    /// <param name="exitCode">Exit code after replaying <paramref name="fixture"/>.</param>
    /// <param name="sleepSeconds">Seconds to sleep before replaying, to test timeouts.</param>
    public static FakeClaudeCli Create(string fixture, string? resumeFixture = null, int exitCode = 0, int sleepSeconds = 0)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Claude CLI is a POSIX shell script; use it from a UnixFact.");
        }

        var directory = System.IO.Directory.CreateTempSubdirectory("fake-claude").FullName;
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Agents", "Cli", "Fixtures");
        var resumeBranch = resumeFixture is null
            ? string.Empty
            : $"""
              case " $* " in *" --resume "*)
                cat '{Path.Combine(fixtures, resumeFixture)}'
                echo 'No conversation found with session ID: x' >&2
                exit 1;;
              esac
              """;

        // pid.txt is written through a temporary file and renamed, so a reader never sees it half written.
        var script = $"""
            #!/bin/sh
            echo $$ > '{directory}/pid.tmp'
            mv '{directory}/pid.tmp' '{directory}/pid.txt'
            env > '{directory}/environment.txt'
            for argument in "$@"; do printf '%s\n' "$argument"; done >> '{directory}/arguments.txt'
            printf '%s\n' '---END---' >> '{directory}/arguments.txt'
            previous=''
            for argument in "$@"; do
              if [ "$previous" = '--mcp-config' ]; then
                cp "$argument" '{directory}/mcp.json'
                stat -c %a "$argument" > '{directory}/mcp-mode.txt' 2>/dev/null || stat -f %Lp "$argument" > '{directory}/mcp-mode.txt'
              fi
              previous="$argument"
            done
            cat > '{directory}/stdin.txt'
            if [ {sleepSeconds} -gt 0 ]; then sleep {sleepSeconds}; fi
            {resumeBranch}
            cat '{Path.Combine(fixtures, fixture)}'
            exit {exitCode}
            """;

        var scriptPath = Path.Combine(directory, "claude");
        File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FakeClaudeCli(directory, scriptPath);
    }
}
