using HomeBlaze.Abstractions;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The roles named by the agents of the graph, started or not, so that the role store validates a change against them
/// and renames a role in them.
/// </summary>
internal sealed class AgentRoleNameReferences(IInterceptorSubjectContext context, RootManager rootManager) : IRoleNameReferences
{
    public IEnumerable<PrincipalRoleSet> GetRoleSets() =>
        GetAgents().Select(agent => new PrincipalRoleSet(agent.Title ?? agent.GetType().Name, agent.Roles)).ToList();

    public bool IsUsed(string roleName) => GetAgents().Exists(agent => NamesRole(agent, roleName));

    /// <summary>
    /// Renames the role in every agent naming it and writes the configuration of each, also when another failed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration of an agent could not be written; names the agents.</exception>
    public async Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        List<string>? failures = null;
        foreach (var agent in GetAgents().Where(agent => NamesRole(agent, oldName)))
        {
            // A new list, as the agent notices only a replaced one.
            agent.Roles = [.. agent.Roles.Select(name => string.Equals(name, oldName, StringComparison.Ordinal) ? newName : name)];
            try
            {
                if (!await WriteConfigurationAsync(agent, cancellationToken))
                {
                    (failures ??= []).Add($"{agent.Title}: its configuration was not written");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                (failures ??= []).Add($"{agent.Title}: {exception.Message}");
            }
        }

        if (failures is not null)
        {
            throw new InvalidOperationException($"The new role name could not be saved for {string.Join("; ", failures)}.");
        }
    }

    private static bool NamesRole(LlmAgentBase agent, string roleName) =>
        agent.Roles.Contains(roleName, StringComparer.Ordinal);

    private List<LlmAgentBase> GetAgents() =>
        context.TryGetService<ISubjectRegistry>() is { } registry
            ? registry.KnownSubjects.Keys.OfType<LlmAgentBase>().ToList()
            : [];

    // As an edit dialog persists a subject: the configuration that holds the agent is written and applied.
    private async Task<bool> WriteConfigurationAsync(LlmAgentBase agent, CancellationToken cancellationToken)
    {
        if (agent.TryGetConfigurationOwner() is not { } owner)
        {
            return false;
        }

        var isWritten = await rootManager.GetConfigurationWriter(owner).WriteConfigurationAsync(owner, CancellationToken.None);
        if (owner is IConfigurable configurable)
        {
            await configurable.ApplyConfigurationAsync(cancellationToken);
        }

        return isWritten;
    }
}
