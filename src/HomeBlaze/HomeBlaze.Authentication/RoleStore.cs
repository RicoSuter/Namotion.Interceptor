using HomeBlaze.Authentication.Data;
using HomeBlaze.Authentication.Internal;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Namotion.Interceptor.Authorization;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>
/// Stores custom roles in the authentication database and projects them into <see cref="AuthorizationSubject.Roles"/>.
/// A change is validated against the roles of every principal (the users, those of every
/// <see cref="IRoleNameReferences"/> and those recorded with <see cref="AuthorizationService.SetPrincipalRoles"/>) and
/// committed before the projection changes. A change is also refused when it would leave no unlocked user holding
/// <see cref="BuiltInRoles.EverythingName"/> without a deny rule on <c>administer</c> while one does now. Changes run one at a time, together with the changes of
/// users, through the <see cref="AuthorizationChangeGate"/>. Thread-safe.
/// </summary>
/// <remarks>
/// A rename is validated as if every principal had the new name. Users hold their roles by database row, so the rename
/// reaches them with the row; every other principal is renamed only through an <see cref="IRoleNameReferences"/>, so
/// every principal recorded with <see cref="AuthorizationService.SetPrincipalRoles"/> must also be held by one.
/// </remarks>
public sealed class RoleStore : IRoleStore
{
    private static readonly UpperInvariantLookupNormalizer LookupNormalizer = new();

    private readonly IDbContextFactory<AuthenticationDbContext> _contextFactory;
    private readonly AuthorizationService _authorizationService;
    private readonly IReadOnlyList<IRoleNameReferences> _references;
    private readonly Func<IEnumerable<PrincipalRoleSet>> _userRoleSets;
    private readonly Func<IReadOnlyCollection<PersonRoleSet>> _personRoleSets;
    private readonly AuthorizationProjection _projection;
    private readonly AuthorizationChangeGate _gate;

    // Replaced as a whole under _gate, never changed in place, so it is read without the gate.
    private volatile Dictionary<string, (AuthorizationRole Role, string Stamp)> _roles = new(StringComparer.Ordinal);

    /// <param name="contextFactory">Creates a context of the authentication database per operation.</param>
    /// <param name="authorizationService">Validates a change, and records the principals of agents.</param>
    /// <param name="references">The places outside the database that name roles.</param>
    /// <param name="userRoleSets">Gets the role names of every user.</param>
    /// <param name="personRoleSets">Gets the role names of every user who is a person, and whether they are locked out.</param>
    /// <param name="projection">Shows the stored roles in the graph.</param>
    /// <param name="gate">Runs the changes of roles and users one at a time.</param>
    public RoleStore(
        IDbContextFactory<AuthenticationDbContext> contextFactory,
        AuthorizationService authorizationService,
        IEnumerable<IRoleNameReferences> references,
        Func<IEnumerable<PrincipalRoleSet>> userRoleSets,
        Func<IReadOnlyCollection<PersonRoleSet>> personRoleSets,
        AuthorizationProjection projection,
        AuthorizationChangeGate gate)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(userRoleSets);
        ArgumentNullException.ThrowIfNull(personRoleSets);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(gate);

        _contextFactory = contextFactory;
        _authorizationService = authorizationService;
        _references = [.. references];
        _userRoleSets = userRoleSets;
        _personRoleSets = personRoleSets;
        _projection = projection;
        _gate = gate;
    }

    /// <summary>Logs every committed change.</summary>
    internal AuthenticationEventLog EventLog { get; init; } = AuthenticationEventLog.Silent;

    /// <summary>
    /// Raised under the <see cref="AuthorizationChangeGate"/> once a rename is committed and projected, with the old and
    /// the new name, before the principals outside the database are renamed. Users already hold the new name in the
    /// database, through the role row.
    /// </summary>
    internal event Action<string, string>? RoleRenamed;

    /// <summary>
    /// Gets the name of the built-in role or stored custom role named <paramref name="name"/>, or null when there is none.
    /// </summary>
    public string? GetRoleName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (BuiltInRoles.All.FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.Ordinal)) is { } builtIn)
        {
            return builtIn.Name;
        }

        return _roles.TryGetValue(name, out var stored) ? stored.Role.Name : null;
    }

    /// <summary>Loads the custom roles from the database and projects them.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = _contextFactory.CreateDbContext();
            var rows = await context.Roles.AsNoTracking().Where(role => !role.IsBuiltIn).ToListAsync(cancellationToken);
            var roles = new Dictionary<string, (AuthorizationRole Role, string Stamp)>(rows.Count, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.Name is { } name)
                {
                    roles[name] = (RoleDefinition.Deserialize(row.Definition).ToRole(name), row.ConcurrencyStamp ?? string.Empty);
                }
            }

            Publish(roles);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public string? GetConcurrencyStamp(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _roles.TryGetValue(name, out var entry) ? entry.Stamp : null;
    }

    /// <inheritdoc />
    public async Task<RoleChangeResult> SaveRoleAsync(string? originalName, AuthorizationRole role, string? concurrencyStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (AuthorizationService.GetRoleNameError(role.Name) is { } nameError)
        {
            return new RoleChangeResult(false, nameError);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await SaveCoreAsync(originalName, role, concurrencyStamp, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RoleChangeResult> RenameRoleAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);

        if (AuthorizationService.GetRoleNameError(newName) is { } nameError)
        {
            return new RoleChangeResult(false, nameError);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_roles.TryGetValue(oldName, out var stored))
            {
                return new RoleChangeResult(false, $"The role '{oldName}' no longer exists.");
            }

            return await SaveCoreAsync(oldName, stored.Role with { Name = newName }, stored.Stamp, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RoleChangeResult> DeleteRoleAsync(string name, string? concurrencyStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var roles = _roles;
            if (!roles.TryGetValue(name, out var loaded) || loaded.Stamp != concurrencyStamp)
            {
                return new RoleChangeResult(false, GetChangedMessage(name));
            }

            if (_references.Any(references => references.IsUsed(name)) ||
                _userRoleSets().Any(roleSet => roleSet.RoleNames.Contains(name, StringComparer.Ordinal)))
            {
                return new RoleChangeResult(false, $"The role '{name}' is used by users or agents; remove it from them first.");
            }

            var updated = new Dictionary<string, (AuthorizationRole Role, string Stamp)>(roles, StringComparer.Ordinal);
            updated.Remove(name);
            var current = roles.Values.Select(entry => entry.Role).ToList();
            var candidate = updated.Values.Select(entry => entry.Role).ToList();
            var validationError = _authorizationService.ValidateRoles(current, candidate, GetPrincipalRoleSets())
                ?? FullAdministrators.GetRemovalError(current, candidate, _personRoleSets(), rename: null);
            if (validationError is not null)
            {
                return new RoleChangeResult(false, validationError);
            }

            await using (var context = _contextFactory.CreateDbContext())
            {
                var normalizedName = Normalize(name);
                var row = await context.Roles.SingleOrDefaultAsync(r => r.NormalizedName == normalizedName && !r.IsBuiltIn, cancellationToken);
                if (row is null || row.ConcurrencyStamp != concurrencyStamp)
                {
                    return new RoleChangeResult(false, GetChangedMessage(name));
                }

                context.Roles.Remove(row);
                await context.SaveChangesAsync(cancellationToken);
            }

            Publish(updated);
            EventLog.RoleDeleted(name);
            return new RoleChangeResult(true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new RoleChangeResult(false, GetChangedMessage(name));
        }
        finally
        {
            _gate.Release();
        }
    }

    // The caller holds _gate.
    private async Task<RoleChangeResult> SaveCoreAsync(string? originalName, AuthorizationRole role, string? concurrencyStamp, CancellationToken cancellationToken)
    {
        try
        {
            var roles = _roles;
            var renamedFrom = originalName is not null && !string.Equals(originalName, role.Name, StringComparison.Ordinal)
                ? originalName
                : null;
            if (GetSaveError(roles, originalName, role, concurrencyStamp, renamedFrom) is { } error)
            {
                return new RoleChangeResult(false, error);
            }

            var changes = GetChanges(roles, originalName, role, renamedFrom);
            if (ValidateChanges(roles, changes, renamedFrom, role.Name) is { } validationError)
            {
                return new RoleChangeResult(false, validationError);
            }

            var (stamps, changedRoleName) = await WriteChangesAsync(roles, changes, cancellationToken);
            if (stamps is null)
            {
                return new RoleChangeResult(false, GetChangedMessage(changedRoleName!));
            }

            // Committed: the projection is built from what was written, and what follows must not be canceled. The users
            // hold the new name before the roles are projected, so that the policies compiled after the projection, which
            // open circuits follow, find the renamed role under the name their users hold.
            var usersWarning = renamedFrom is null ? null : RaiseRoleRenamed(renamedFrom, role.Name);
            Publish(ApplyChanges(roles, changes, stamps));
            EventLog.RoleSaved(role.Name, originalName, renamedFrom);

            var referencesWarning = renamedFrom is null ? null : await RenameReferencesAsync(renamedFrom, role.Name);
            return new RoleChangeResult(true, null, JoinWarnings(usersWarning, referencesWarning), stamps[role.Name]);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new RoleChangeResult(false, GetChangedMessage(originalName ?? role.Name));
        }
    }

    private static string GetChangedMessage(string name) => $"The role '{name}' changed since you opened it.";

    // Identity's default normalizer, as the user manager finds roles by this name when it assigns them.
    private static string Normalize(string name) => LookupNormalizer.NormalizeName(name);

    private static string? GetSaveError(
        Dictionary<string, (AuthorizationRole Role, string Stamp)> roles,
        string? originalName,
        AuthorizationRole role,
        string? concurrencyStamp,
        string? renamedFrom)
    {
        if (originalName is not null && (!roles.TryGetValue(originalName, out var loaded) || loaded.Stamp != concurrencyStamp))
        {
            return GetChangedMessage(originalName);
        }

        return (originalName is null || renamedFrom is not null) && roles.ContainsKey(role.Name)
            ? $"A role named '{role.Name}' already exists."
            : null;
    }

    private static Dictionary<string, (AuthorizationRole Role, string Stamp)> ApplyChanges(
        Dictionary<string, (AuthorizationRole Role, string Stamp)> roles,
        List<(string? OriginalName, AuthorizationRole Role)> changes,
        Dictionary<string, string>? stamps)
    {
        var updated = new Dictionary<string, (AuthorizationRole Role, string Stamp)>(roles, StringComparer.Ordinal);
        foreach (var (originalName, _) in changes)
        {
            if (originalName is not null)
            {
                updated.Remove(originalName);
            }
        }

        foreach (var (_, role) in changes)
        {
            updated[role.Name] = (role, stamps?[role.Name] ?? string.Empty);
        }

        return updated;
    }

    private string? ValidateChanges(
        Dictionary<string, (AuthorizationRole Role, string Stamp)> roles,
        List<(string? OriginalName, AuthorizationRole Role)> changes,
        string? renamedFrom,
        string newName)
    {
        var current = roles.Values.Select(entry => entry.Role).ToList();
        var candidate = ApplyChanges(roles, changes, stamps: null).Values.Select(entry => entry.Role).ToList();
        var principalRoleSets = GetPrincipalRoleSets();
        var error = renamedFrom is null
            ? _authorizationService.ValidateRoles(current, candidate, principalRoleSets)
            : _authorizationService.ValidateRoles(current, candidate, principalRoleSets, renamedFrom, newName);
        return error ?? FullAdministrators.GetRemovalError(
            current, candidate, _personRoleSets(), renamedFrom is null ? null : (renamedFrom, newName));
    }

    // The saved role and, for a rename, every other custom role whose includes name the old name, with the new name.
    private static List<(string? OriginalName, AuthorizationRole Role)> GetChanges(
        Dictionary<string, (AuthorizationRole Role, string Stamp)> roles,
        string? originalName,
        AuthorizationRole role,
        string? renamedFrom)
    {
        var changes = new List<(string? OriginalName, AuthorizationRole Role)> { (originalName, role) };
        if (renamedFrom is null)
        {
            return changes;
        }

        foreach (var (name, (existing, _)) in roles)
        {
            if (!string.Equals(name, renamedFrom, StringComparison.Ordinal) &&
                existing.Includes.Contains(renamedFrom, StringComparer.Ordinal))
            {
                var includes = existing.Includes
                    .Select(include => string.Equals(include, renamedFrom, StringComparison.Ordinal) ? role.Name : include);
                changes.Add((name, existing with { Includes = [.. includes] }));
            }
        }

        return changes;
    }

    // Writes every change in one transaction and returns the new stamps by name, or nothing and the name of the first
    // stored role that is not the one loaded with its stamp, or is gone.
    private async Task<(Dictionary<string, string>? Stamps, string? ChangedRoleName)> WriteChangesAsync(
        Dictionary<string, (AuthorizationRole Role, string Stamp)> roles,
        List<(string? OriginalName, AuthorizationRole Role)> changes,
        CancellationToken cancellationToken)
    {
        await using var context = _contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (originalName, role) in changes)
        {
            ApplicationRole row;
            if (originalName is null)
            {
                row = new ApplicationRole();
                context.Roles.Add(row);
            }
            else
            {
                var normalizedOriginalName = Normalize(originalName);
                var existing = await context.Roles.SingleOrDefaultAsync(r => r.NormalizedName == normalizedOriginalName && !r.IsBuiltIn, cancellationToken);
                if (existing is null || existing.ConcurrencyStamp != roles[originalName].Stamp)
                {
                    return (null, originalName);
                }

                row = existing;
            }

            row.Name = role.Name;
            row.NormalizedName = Normalize(role.Name);
            row.Definition = RoleDefinition.From(role).Serialize();
            row.ConcurrencyStamp = Guid.NewGuid().ToString();
            stamps[role.Name] = row.ConcurrencyStamp;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (stamps, null);
    }

    private static string? JoinWarnings(string? first, string? second) =>
        first is null || second is null ? first ?? second : $"{first} {second}";

    // The caller holds _gate. A failing handler is logged and returned as a warning, so that the committed rename is
    // still projected.
    private string? RaiseRoleRenamed(string oldName, string newName)
    {
        try
        {
            RoleRenamed?.Invoke(oldName, newName);
            return null;
        }
        catch (Exception exception)
        {
            EventLog.RoleRenamedHandlerFailed(exception);
            return $"The role was renamed to '{newName}', but the users may show '{oldName}' until the next start: {exception.Message}";
        }
    }

    // Returns what could not be renamed, after trying every place; the role is already renamed in the database.
    private async Task<string?> RenameReferencesAsync(string oldName, string newName)
    {
        List<string>? errors = null;
        foreach (var references in _references)
        {
            try
            {
                await references.RenameAsync(oldName, newName, CancellationToken.None);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception.Message);
            }
        }

        return errors is null
            ? null
            : $"The role was renamed to '{newName}' and the principals use the new name, but it could not be saved everywhere: {string.Join(" ", errors)} Their saved configuration may still name '{oldName}', which applies after a restart.";
    }

    // The caller holds _gate. Projected before the stamps are published: an editor that reads a new stamp then finds the
    // role it loaded replaced, and one that reads an old stamp is refused as changed. Ordered by name, so the order of
    // the roles does not depend on the order of the changes.
    private void Publish(Dictionary<string, (AuthorizationRole Role, string Stamp)> roles)
    {
        _projection.SetRoles([.. roles.Values.Select(entry => entry.Role).OrderBy(role => role.Name, StringComparer.Ordinal)]);
        _roles = roles;
    }

    private List<PrincipalRoleSet> GetPrincipalRoleSets() =>
        _references
            .SelectMany(references => references.GetRoleSets())
            .Concat(_userRoleSets())
            .Concat(_authorizationService.GetPrincipalRoleSets())
            .ToList();
}
