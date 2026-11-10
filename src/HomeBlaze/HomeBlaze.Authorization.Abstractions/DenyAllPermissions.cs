using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authorization.Abstractions;

/// <summary>The permissions used when no authentication is configured: the user may do and see nothing and holds no role.</summary>
public sealed class DenyAllPermissions : IUserPermissions
{
    /// <summary>The shared instance.</summary>
    public static DenyAllPermissions Instance { get; } = new();

    private DenyAllPermissions()
    {
    }

    /// <summary>Never raised: the answers never change.</summary>
    public event Action? Changed
    {
        add
        {
            // Nothing to subscribe to: the answers never change.
        }

        remove
        {
            // Nothing was subscribed.
        }
    }

    /// <inheritdoc />
    public bool CanRead(RegisteredSubjectProperty member) => false;

    /// <inheritdoc />
    public bool CanWrite(RegisteredSubjectProperty member) => false;

    /// <inheritdoc />
    public bool CanInvoke(RegisteredSubjectProperty member) => false;

    /// <inheritdoc />
    public bool CanAdminister(RegisteredSubjectProperty member) => false;

    /// <inheritdoc />
    public bool CanReadHistory(RegisteredSubjectProperty member) => false;

    /// <inheritdoc />
    public bool CanShow(RegisteredSubject subject) => false;

    /// <inheritdoc />
    public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => false;

    /// <inheritdoc />
    public bool CanActThrough(IReadOnlyCollection<string> roles) => false;

    /// <inheritdoc />
    public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];
}
