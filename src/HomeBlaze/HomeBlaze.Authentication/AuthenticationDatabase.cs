using HomeBlaze.Authentication.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication;

/// <summary>
/// Opens and migrates the authentication database once at startup and records whether it can be used. Every page
/// shows "authentication unavailable" while it cannot, instead of opening up.
/// </summary>
public sealed class AuthenticationDatabase
{
    private readonly Func<AuthenticationDbContext> _createContext;
    private readonly ILogger<AuthenticationDatabase> _logger;
    private volatile string? _initializationError = "The authentication database is not initialized yet.";

    // Kept apart from _initializationError, so that no initialization clears it.
    private volatile string? _markedReason;

    private volatile bool _isReady;

    public AuthenticationDatabase(string path, Func<AuthenticationDbContext> createContext, ILogger<AuthenticationDatabase> logger)
    {
        Path = path;
        _createContext = createContext;
        _logger = logger;
    }

    /// <summary>The path of the database file.</summary>
    public string Path { get; }

    /// <summary>Whether the database was opened and migrated and nothing marked it unavailable.</summary>
    public bool IsAvailable => UnavailableReason is null;

    /// <summary>Why the database cannot be used, or null. Shown to visitors who are not signed in, so it holds no details of errors.</summary>
    public string? UnavailableReason => _markedReason ?? _initializationError;

    /// <summary>
    /// Whether the database is available and the startup has seeded it and loaded its roles and users, see
    /// <see cref="MarkReady"/>. Until then, and whenever the database is unavailable, every page shows "authentication
    /// unavailable".
    /// </summary>
    public bool IsReady => _isReady && IsAvailable;

    /// <summary>Records that the startup has completed; does not make an unavailable database available.</summary>
    public void MarkReady() => _isReady = true;

    /// <summary>Creates the directory, opens the database and applies pending migrations; a failure is logged and recorded.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            await using var context = _createContext();
            await context.Database.MigrateAsync(cancellationToken);
            _initializationError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The authentication database at {Path} cannot be used.", Path);
            // Without the message of the exception, which is logged: the reason is shown to visitors who are not signed in.
            _initializationError = "The authentication database cannot be opened or migrated.";
        }
    }

    /// <summary>
    /// Marks the database unavailable for the lifetime of this instance, with a <paramref name="reason"/> that is shown to
    /// visitors who are not signed in and so names no details of errors, for example when the seed marker exists but no
    /// user does; a later initialization does not clear it.
    /// </summary>
    public void MarkUnavailable(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        _markedReason = reason;
    }
}
