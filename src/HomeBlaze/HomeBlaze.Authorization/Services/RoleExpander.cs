using System.Collections.Concurrent;
using System.Collections.Immutable;
using HomeBlaze.Authorization.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Expands roles according to the role hierarchy stored in the database.
/// Caches the role composition for performance.
/// </summary>
public class RoleExpander : IRoleExpander
{
    private readonly IServiceProvider _serviceProvider;
    private ConcurrentDictionary<string, ImmutableHashSet<string>> _roleCompositionCache = new();
    private volatile bool _isInitialized;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public RoleExpander(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public bool IsInitialized => _isInitialized;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (!_isInitialized)
            {
                await LoadCompositionFromDatabaseAsync();
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlySet<string> ExpandRoles(IEnumerable<string> roles)
    {
        // If not initialized yet (startup race condition), return roles as-is
        // This is safe because Anonymous role will still work, and the hierarchy
        // will be available on subsequent requests after seeding completes
        if (!_isInitialized)
        {
            return roles.ToHashSet();
        }

        var result = ImmutableHashSet.CreateBuilder<string>();
        var visited = new HashSet<string>();

        foreach (var role in roles)
        {
            ExpandRoleRecursive(role, result, visited);
        }

        return result.ToImmutable();
    }

    public async Task ReloadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            await LoadCompositionFromDatabaseAsync();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoadCompositionFromDatabaseAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();

        var compositions = await dbContext.RoleCompositions.ToListAsync();
        var newCache = new ConcurrentDictionary<string, ImmutableHashSet<string>>();

        // Build composition map: role -> directly included roles
        var compositionMap = compositions
            .GroupBy(c => c.RoleName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => c.IncludesRole).ToList());

        // Pre-expand each role and cache the result
        foreach (var roleName in compositionMap.Keys)
        {
            var expanded = ExpandSingleRole(roleName, compositionMap);
            newCache[roleName] = expanded;
        }

        _roleCompositionCache = newCache;
        _isInitialized = true;
    }

    private ImmutableHashSet<string> ExpandSingleRole(
        string roleName,
        Dictionary<string, List<string>> compositionMap)
    {
        var result = ImmutableHashSet.CreateBuilder<string>();
        var visited = new HashSet<string>();

        ExpandWithCycleDetection(roleName, compositionMap, result, visited);

        return result.ToImmutable();
    }

    private void ExpandWithCycleDetection(
        string roleName,
        Dictionary<string, List<string>> compositionMap,
        ImmutableHashSet<string>.Builder result,
        HashSet<string> visited)
    {
        // Already visited - stop to prevent infinite loops (handles cycles gracefully)
        if (!visited.Add(roleName))
        {
            return;
        }

        result.Add(roleName);

        if (!compositionMap.TryGetValue(roleName, out var includedRoles))
        {
            return;
        }

        foreach (var includedRole in includedRoles)
        {
            ExpandWithCycleDetection(includedRole, compositionMap, result, visited);
        }
    }

    private void ExpandRoleRecursive(
        string roleName,
        ImmutableHashSet<string>.Builder result,
        HashSet<string> visited)
    {
        // Always include the role itself
        result.Add(roleName);

        if (!visited.Add(roleName))
        {
            return;
        }

        // If we have cached expansion, use it
        if (_roleCompositionCache.TryGetValue(roleName, out var cachedExpansion))
        {
            result.UnionWith(cachedExpansion);
            return;
        }

        // Role not in cache = unknown role, just include itself (already added above)
    }
}
