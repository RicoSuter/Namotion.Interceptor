using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>The roles of HomeBlaze over a test graph, with custom roles in an <c>Authorization</c> subject at <c>/Authorization</c>.</summary>
internal static class TestAuthorization
{
    /// <summary>The name of the role of <see cref="DenyOpen"/>.</summary>
    public const string StormBlinds = "StormBlinds";

    /// <summary>Creates the authorizer of automations over the roles of the graph, and adds the custom roles when given.</summary>
    public static RoleAutomationAuthorizer Create(TestGraph graph, params (string Name, Role Role)[] roles)
    {
        var service = CreateService(graph);
        if (roles.Length > 0)
        {
            AddRoles(graph, roles);
        }

        return new RoleAutomationAuthorizer(service);
    }

    /// <summary>The roles of HomeBlaze over the graph, with paths separated by <c>/</c>; the provider of the tools of agents is internal to another project.</summary>
    public static AuthorizationService CreateService(TestGraph graph) => new(graph.Context, new DefaultPathProvider('/'));

    /// <summary>Attaches an <c>Authorization</c> subject with the custom roles under <c>/Authorization</c>, replacing an earlier one.</summary>
    public static void AddRoles(TestGraph graph, params (string Name, Role Role)[] roles) =>
        graph.Add("Authorization", new global::HomeBlaze.Authorization.Authorization
        {
            Roles = roles.ToDictionary(role => role.Name, role => role.Role, StringComparer.OrdinalIgnoreCase)
        });

    /// <summary>A role that includes <c>Operator</c> and denies every member under the path of <c>/Blinds</c>.</summary>
    public static Role DenyBlindsPath() => new()
    {
        Includes = [BuiltInRoles.OperatorName],
        Deny = [new PermissionRule { Paths = ["/Blinds/**"], Actions = [AuthorizationActions.Execute, PermissionAction.Write] }]
    };

    /// <summary>A role that includes <c>Operator</c> and denies calling <c>Open</c> of the test blinds.</summary>
    public static Role DenyOpen() => new()
    {
        Includes = [BuiltInRoles.OperatorName],
        Deny = [new PermissionRule { Types = [typeof(TestBlinds).FullName!], Members = ["Open"], Actions = [AuthorizationActions.Execute] }]
    };
}
