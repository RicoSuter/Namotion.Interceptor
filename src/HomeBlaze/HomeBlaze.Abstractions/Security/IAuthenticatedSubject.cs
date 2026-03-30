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
    [State]
    bool IsAuthenticated { get; }

    /// <summary>
    /// The authenticated user name or identifier.
    /// </summary>
    [State]
    string? AuthenticatedUser { get; }

    /// <summary>
    /// When authentication occurred.
    /// </summary>
    [State]
    DateTimeOffset? AuthenticatedAt { get; }
}
