using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authorization.Abstractions;

/// <summary>The permissions for tests and tools that show the graph without a person: everything is allowed.</summary>
public sealed class UnrestrictedPermissions : IUserPermissions
{
    /// <summary>The shared instance.</summary>
    public static UnrestrictedPermissions Instance { get; } = new();

    private UnrestrictedPermissions()
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
    public bool CanRead(RegisteredSubjectProperty member) => true;

    /// <inheritdoc />
    public bool CanWrite(RegisteredSubjectProperty member) => true;

    /// <inheritdoc />
    public bool CanInvoke(RegisteredSubjectProperty member) => true;

    /// <inheritdoc />
    public bool CanAdminister(RegisteredSubjectProperty member) => true;

    /// <inheritdoc />
    public bool CanReadHistory(RegisteredSubjectProperty member) => true;

    /// <inheritdoc />
    public bool CanShow(RegisteredSubject subject) => true;

    /// <inheritdoc />
    public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => true;

    /// <inheritdoc />
    public bool CanActThrough(IReadOnlyCollection<string> roles) => true;

    /// <inheritdoc />
    public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];
}
