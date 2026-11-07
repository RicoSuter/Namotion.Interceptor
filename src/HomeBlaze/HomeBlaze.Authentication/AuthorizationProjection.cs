using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>
/// Holds the roles and users loaded from the database and writes them into the single <see cref="AuthorizationSubject"/>
/// subject of the graph whenever they change or the subject is attached. Thread-safe.
/// </summary>
public sealed class AuthorizationProjection
{
    private readonly Lock _lock = new();
    private IReadOnlyCollection<AuthorizationRole> _roles = [];
    private IReadOnlyCollection<UserSnapshot> _users = [];
    private AuthorizationSubject? _target;

    /// <summary>The subject the projection writes to, or null when none is attached.</summary>
    internal AuthorizationSubject? Target
    {
        get
        {
            lock (_lock)
            {
                return _target;
            }
        }
    }

    /// <summary>
    /// Sets the subject the projection writes to and writes the roles and users to it; null detaches it. Attaching the
    /// subject that is attached already changes nothing, so its roles and users stay the same instances.
    /// </summary>
    public void Attach(AuthorizationSubject? target)
    {
        lock (_lock)
        {
            // A late attach notice must not replace the users while an administrator acts on the instances it read.
            if (ReferenceEquals(_target, target))
            {
                return;
            }

            _target = target;
            target?.ReplaceRoles(_roles);
            target?.ReplaceUsers(_users);
        }
    }

    /// <summary>Sets the roles and writes them to the attached subject.</summary>
    public void SetRoles(IReadOnlyCollection<AuthorizationRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        lock (_lock)
        {
            _roles = roles;
            _target?.ReplaceRoles(roles);
        }
    }

    /// <summary>Sets the users and writes them to the attached subject.</summary>
    public void SetUsers(IReadOnlyCollection<UserSnapshot> users)
    {
        ArgumentNullException.ThrowIfNull(users);

        lock (_lock)
        {
            _users = users;
            _target?.ReplaceUsers(users);
        }
    }
}
