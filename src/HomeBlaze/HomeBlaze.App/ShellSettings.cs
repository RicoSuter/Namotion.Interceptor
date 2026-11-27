namespace HomeBlaze.App;

/// <summary>How a connection reaches a HomeBlaze instance.</summary>
public enum ConnectionKind
{
    /// <summary>An instance that runs elsewhere, addressed by URL.</summary>
    Remote,

    /// <summary>A folder that holds, or will hold, a complete HomeBlaze data directory.</summary>
    FullDataFolder,

    /// <summary>A plain folder of files, mounted without writing HomeBlaze bookkeeping into it.</summary>
    MarkdownFolder
}

/// <summary>One saved way to reach a HomeBlaze instance.</summary>
public sealed record ShellConnection
{
    /// <summary>Stable identifier, used to remember the last used connection.</summary>
    public required string Id { get; init; }

    /// <summary>Name shown on the bootstrap screen.</summary>
    public required string Name { get; init; }

    public required ConnectionKind Kind { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="ConnectionKind.Remote"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Set for both folder kinds. Always an absolute path.</summary>
    public string? FolderPath { get; init; }
}

/// <summary>Window geometry carried across restarts.</summary>
public sealed record ShellWindowState
{
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 860;

    /// <summary>Null means the operating system picks the position.</summary>
    public int? Left { get; init; }

    /// <summary>Null means the operating system picks the position.</summary>
    public int? Top { get; init; }

    public bool FullScreen { get; init; }
}

/// <summary>Everything the shell remembers between runs.</summary>
public sealed record ShellSettings
{
    public IReadOnlyList<ShellConnection> Connections { get; init; } = [];

    /// <summary>Identifier of the connection to open on start, or null to show the bootstrap screen.</summary>
    public string? LastConnectionId { get; init; }

    public ShellWindowState Window { get; init; } = new();
}
