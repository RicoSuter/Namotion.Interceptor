namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Resolves role names to the roles they name and every role those include, each once, included roles first.</summary>
internal static class RoleClosure
{
    private const int MaxDepth = 64;

    public static List<AuthorizationRole> Resolve(IReadOnlyCollection<AuthorizationRole> roles, IReadOnlyCollection<string> roleNames)
    {
        var rolesByName = new Dictionary<string, AuthorizationRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            if (role is null)
            {
                throw new AuthorizationException("A role is null.");
            }

            if (string.IsNullOrWhiteSpace(role.Name))
            {
                throw new AuthorizationException("A role has no name.");
            }

            if (string.Equals(role.Name, AuthorizationPolicy.SystemRoleName, StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthorizationException($"Role name '{AuthorizationPolicy.SystemRoleName}' is reserved.");
            }

            if (!rolesByName.TryAdd(role.Name, role))
            {
                throw new AuthorizationException($"Role '{role.Name}' is defined more than once.");
            }
        }

        var result = new List<AuthorizationRole>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        foreach (var name in roleNames)
        {
            Visit(name, rolesByName, result, visited, path);
        }

        return result;
    }

    private static void Visit(
        string? name,
        Dictionary<string, AuthorizationRole> rolesByName,
        List<AuthorizationRole> result,
        HashSet<string> visited,
        List<string> path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AuthorizationException(path.Count == 0
                ? "A role name is empty."
                : $"Role '{path[^1]}' includes a role with no name.");
        }

        if (!rolesByName.TryGetValue(name, out var role))
        {
            throw new AuthorizationException(path.Count == 0
                ? $"Role '{name}' is not defined."
                : $"Role '{path[^1]}' includes '{name}', which is not defined.");
        }

        var cycleStart = path.FindIndex(entry => string.Equals(entry, role.Name, StringComparison.OrdinalIgnoreCase));
        if (cycleStart >= 0)
        {
            var cycle = string.Join(" > ", path.Skip(cycleStart).Append(role.Name));
            throw new AuthorizationException($"Role '{role.Name}' includes itself: {cycle}.");
        }

        if (!visited.Add(role.Name))
        {
            return;
        }

        path.Add(role.Name);
        if (path.Count > MaxDepth)
        {
            throw new AuthorizationException($"Role '{role.Name}' is nested more than {MaxDepth} levels deep.");
        }

        if (!role.Includes.IsDefaultOrEmpty)
        {
            foreach (var include in role.Includes)
            {
                Visit(include, rolesByName, result, visited, path);
            }
        }

        path.RemoveAt(path.Count - 1);
        result.Add(role);
    }
}
