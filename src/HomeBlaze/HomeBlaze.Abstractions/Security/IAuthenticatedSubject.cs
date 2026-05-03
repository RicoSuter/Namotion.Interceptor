using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Security;

/// <summary>
/// Interface for subjects that track authentication status.
/// </summary>
[SubjectAbstraction]
[Description("Tracks authentication status, user identity, and authentication time.")]
public interface IAuthenticatedSubject
{
    /// <summary>
    /// Whether the subject is currently authenticated.
    /// </summary>
    [State(Position = 870)]
    bool IsAuthenticated { get; }

    /// <summary>
    /// The authenticated user name or identifier.
    /// </summary>
    [State(Position = 871)]
    string? AuthenticatedUser { get; }

    /// <summary>
    /// When authentication occurred.
    /// </summary>
    [State(Position = 872)]
    DateTimeOffset? AuthenticatedAt { get; }
}
