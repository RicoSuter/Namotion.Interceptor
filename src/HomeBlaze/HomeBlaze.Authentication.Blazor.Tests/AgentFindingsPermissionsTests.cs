using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Anthropic;
using HomeBlaze.AI.Agents.ClaudeCli;
using HomeBlaze.Authorization;
using HomeBlaze.Components;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class AgentFindingsPermissionsTests
{
    private const string Finding = "The freezer door is open.";

    private static readonly AuthorizationRole FamilyRole = new() { Name = "family" };

    // Includes Operator and denies writing below /Heating.
    private static readonly AuthorizationRole KidRole = new()
    {
        Name = "kid",
        Includes = [BuiltInRoles.OperatorName],
        Deny = [new PermissionRule { Paths = ["/Heating/**"], Actions = [PermissionAction.Write] }]
    };

    [Fact]
    public async Task WhenAReaderLacksTheRolesOfTheAgent_ThenTheLastAnalysisAndItsHistoryAreHidden()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.ReaderName]);
        var agent = AddAgent(graph, [BuiltInRoles.OperatorName]);
        var lastAnalysis = GetMember(agent, nameof(agent.LastAnalysis));

        // Act
        var reading = ValueReader.ReadResolved(new SubjectPathResolver(() => graph.Root), lastAnalysis, null, null, graph.Permissions);

        // Assert
        Assert.True(reading.IsMissing);
        Assert.False(graph.Permissions.CanRead(lastAnalysis));
        Assert.False(graph.Permissions.CanReadHistory(lastAnalysis));
        Assert.True(graph.Permissions.CanRead(GetMember(agent, nameof(agent.Status))));
    }

    [Fact]
    public async Task WhenAPersonHoldsEveryRoleOfTheAgent_ThenTheyReadTheLastAnalysisAndItsHistory()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);
        var agent = AddAgent(graph, [BuiltInRoles.OperatorName]);
        var lastAnalysis = GetMember(agent, nameof(agent.LastAnalysis));

        // Act
        var reading = ValueReader.ReadResolved(new SubjectPathResolver(() => graph.Root), lastAnalysis, null, null, graph.Permissions);

        // Assert
        Assert.Equal(Finding, reading.Value);
        Assert.True(graph.Permissions.CanReadHistory(lastAnalysis));
    }

    [Fact]
    public async Task WhenAnAgentHasNoRoles_ThenOnlyAFullAdministratorReadsTheLastAnalysis()
    {
        // Arrange
        await using var operatorGraph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);
        await using var administratorGraph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName]);
        var operatorAgent = AddAgent(operatorGraph, []);
        var administratorAgent = AddAgent(administratorGraph, []);

        // Act
        var operatorCanRead = operatorGraph.Permissions.CanRead(GetMember(operatorAgent, nameof(operatorAgent.LastAnalysis)));
        var administratorCanRead = administratorGraph.Permissions.CanRead(GetMember(administratorAgent, nameof(administratorAgent.LastAnalysis)));

        // Assert
        Assert.False(operatorCanRead);
        Assert.True(administratorCanRead);
    }

    [Fact]
    public async Task WhenAnOperatorLacksARoleOfTheAgent_ThenTheyCannotStartANewConversation()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName], customRoles: [FamilyRole]);
        var agent = AddAgent(graph, [BuiltInRoles.OperatorName, FamilyRole.Name]);
        var newConversation = GetMember(agent, "NewConversation");
        var canInvokeWithoutFamily = graph.Permissions.CanInvoke(newConversation);

        // Act
        await graph.SetRolesAsync([BuiltInRoles.OperatorName, FamilyRole.Name]);

        // Assert
        Assert.False(canInvokeWithoutFamily);
        Assert.True(graph.Permissions.CanInvoke(newConversation));
    }

    [Fact]
    public async Task WhenAPersonHoldsARoleWithDenyRulesTheAgentLacks_ThenTheyNeitherChatNorReadTheFindings()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [KidRole.Name], customRoles: [KidRole]);
        var agent = AddAgent(graph, [BuiltInRoles.OperatorName]);

        // Act
        var canActThrough = graph.Permissions.CanActThrough(agent.Roles);
        var missing = graph.Permissions.GetRolesWithDenyRulesMissingFrom(agent.Roles);
        var canRead = graph.Permissions.CanRead(GetMember(agent, nameof(agent.LastAnalysis)));

        // Assert
        Assert.True(graph.Permissions.HoldsAllRoles(agent.Roles)); // Kid includes Operator
        Assert.False(canActThrough);
        Assert.Equal([KidRole.Name], missing);
        Assert.False(canRead);
    }

    [Fact]
    public async Task WhenTheAgentHoldsTheRoleWithDenyRulesOfThePerson_ThenTheyChatAndReadTheFindings()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [KidRole.Name], customRoles: [KidRole]);
        var agent = AddAgent(graph, [KidRole.Name]);

        // Act
        var canActThrough = graph.Permissions.CanActThrough(agent.Roles);
        var missing = graph.Permissions.GetRolesWithDenyRulesMissingFrom(agent.Roles);
        var canRead = graph.Permissions.CanRead(GetMember(agent, nameof(agent.LastAnalysis)));

        // Assert
        Assert.True(canActThrough);
        Assert.Empty(missing);
        Assert.True(canRead);
    }

    [Fact]
    public async Task WhenAFullAdministratorHoldsARoleWithDenyRules_ThenTheyChatOnlyWithAgentsHoldingIt()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName, KidRole.Name], customRoles: [KidRole]);

        // Act & Assert
        Assert.False(graph.Permissions.CanActThrough([BuiltInRoles.OperatorName]));
        Assert.True(graph.Permissions.CanActThrough([KidRole.Name]));
        Assert.True(graph.Permissions.CanActThrough([])); // an agent without roles may do nothing
    }

    [Fact]
    public async Task WhenAPersonHoldsNoRoleWithDenyRules_ThenTheyChatWithAgentsHoldingTheirRolesAsBefore()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);

        // Act & Assert
        Assert.True(graph.Permissions.CanActThrough([BuiltInRoles.ReaderName]));
        Assert.True(graph.Permissions.CanActThrough([BuiltInRoles.OperatorName]));
        Assert.False(graph.Permissions.CanActThrough([BuiltInRoles.EverythingName]));
        Assert.False(graph.Permissions.CanActThrough([]));
        Assert.Empty(graph.Permissions.GetRolesWithDenyRulesMissingFrom([BuiltInRoles.ReaderName]));
    }

    [Fact]
    public async Task WhenAFullAdministratorHoldsARoleWithDenyRulesTheAgentLacks_ThenTheyCannotRunItNow()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.EverythingName, KidRole.Name], customRoles: [KidRole]);
        var operatorAgent = AddAgent(graph, [BuiltInRoles.OperatorName]);

        // Act
        var canRunOperatorAgent = graph.Permissions.CanInvoke(GetMember(operatorAgent, nameof(operatorAgent.RunNow)));
        operatorAgent.Roles = [KidRole.Name];
        var canRunKidAgent = graph.Permissions.CanInvoke(GetMember(operatorAgent, nameof(operatorAgent.RunNow)));

        // Assert
        Assert.False(canRunOperatorAgent);
        Assert.True(canRunKidAgent);
    }

    [Fact]
    public async Task WhenTheRolesOfTheAgentOrAGivenListChange_ThenTheAnswerFollowsThem()
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [KidRole.Name], customRoles: [KidRole]);
        var agent = AddAgent(graph, [BuiltInRoles.OperatorName]);
        var lastAnalysis = GetMember(agent, nameof(agent.LastAnalysis));
        var roles = new List<string> { KidRole.Name };

        // Act
        var canReadWithOperator = graph.Permissions.CanRead(lastAnalysis);
        agent.Roles = [KidRole.Name];
        var canReadWithKid = graph.Permissions.CanRead(lastAnalysis);
        var canActThroughKid = graph.Permissions.CanActThrough(roles);
        roles[0] = BuiltInRoles.OperatorName;
        var canActThroughOperator = graph.Permissions.CanActThrough(roles);

        // Assert
        Assert.False(canReadWithOperator);
        Assert.True(canReadWithKid);
        Assert.True(canActThroughKid);
        Assert.False(canActThroughOperator);
    }

    [Theory]
    [InlineData(nameof(PermissionsTestAgent))]
    [InlineData(nameof(ClaudeCliAgent))]
    [InlineData(nameof(AnthropicAgent))]
    public async Task WhenEnumeratingTheMembersOfAnAgent_ThenEachIsRoleScopedOrExplicitlySharedWithAllReaders(string agentType)
    {
        // Arrange
        await using var graph = await PermissionsTestGraph.CreateAsync(roles: [BuiltInRoles.OperatorName]);
        var agent = AddAgent(graph, [BuiltInRoles.OperatorName], CreateAgent(graph, agentType));
        var scoped = (IRoleScopedSubject)agent;

        // A member that shows what the background runs or the shared conversation produced must be role scoped; a new
        // member is added here only when it reveals nothing of them.
        string[] sharedWithAllReaders =
        [
            nameof(agent.Name), nameof(agent.Instructions), nameof(agent.WatchPaths), nameof(agent.PollInterval),
            nameof(agent.MinRunInterval), nameof(agent.AllowedTools), nameof(agent.Roles), nameof(agent.TurnTimeout),
            nameof(agent.MaxToolCallsPerTurn), nameof(agent.Model), nameof(agent.Effort), nameof(agent.BackgroundModel),
            nameof(agent.BackgroundEffort), nameof(agent.Status), nameof(agent.StatusMessage), nameof(agent.IsBusy),
            nameof(agent.LastActivityTime), nameof(agent.LastRunTime), nameof(agent.RunCount), nameof(agent.FiveHourUsage),
            nameof(agent.SevenDayUsage), nameof(agent.Title), nameof(agent.IconName), nameof(agent.IconColor),
            nameof(agent.SupportedBackendTools), nameof(IRoleScopedSubject.ScopeRoles), nameof(agent.IsStorageLoaded),
            "WorkingDirectory", "Services",
            nameof(ClaudeCliAgent.CliPath), nameof(ClaudeCliAgent.FallbackModel),
            nameof(AnthropicAgent.ApiKey), nameof(AnthropicAgent.MaxTokens), nameof(AnthropicAgent.HistoryTokenBudget), "HttpClient"
        ];

        // The attributes that describe a member, not its value.
        string[] sharedAttributes = [KnownAttributes.State, KnownAttributes.Configuration];

        // Act
        var unclassified = agent.TryGetRegisteredSubject()!.Properties
            .Where(member => !scoped.IsRoleScoped(member.Name) &&
                !(member.IsAttribute ? sharedAttributes.Contains(member.AttributeMetadata.AttributeName) : sharedWithAllReaders.Contains(member.Name)))
            .Select(member => member.Name)
            .ToList();

        // Assert
        Assert.Empty(unclassified);
        string[] findings = [nameof(agent.LastAnalysis), LlmAgentBase.NewConversationMemberName, nameof(agent.RunNow), nameof(agent.Timeline), nameof(agent.Memory), nameof(agent.StreamingText)];
        Assert.All(findings, name => Assert.True(scoped.IsRoleScoped(name), name));
    }

    private static LlmAgentBase CreateAgent(PermissionsTestGraph graph, string agentType) => agentType switch
    {
        nameof(ClaudeCliAgent) => new ClaudeCliAgent(PermissionsTestAgent.CreateServices(graph.AuthorizationService), NullLogger<ClaudeCliAgent>.Instance),
        nameof(AnthropicAgent) => new AnthropicAgent(PermissionsTestAgent.CreateServices(graph.AuthorizationService), NullLogger<AnthropicAgent>.Instance),
        _ => new PermissionsTestAgent(graph.AuthorizationService)
    };

    private static PermissionsTestAgent AddAgent(PermissionsTestGraph graph, List<string> roles) =>
        AddAgent(graph, roles, new PermissionsTestAgent(graph.AuthorizationService));

    private static TAgent AddAgent<TAgent>(PermissionsTestGraph graph, List<string> roles, TAgent agent)
        where TAgent : LlmAgentBase
    {
        agent.Roles = roles;
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Agent"] = agent };
        GetMember(agent, nameof(agent.LastAnalysis)).SetValue(Finding);
        return agent;
    }

    private static RegisteredSubjectProperty GetMember(LlmAgentBase agent, string name) =>
        agent.TryGetRegisteredSubject()!.TryGetProperty(name) ?? throw new InvalidOperationException($"The agent has no member {name}.");
}
