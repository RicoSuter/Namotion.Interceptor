using Bunit;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Components.Abstractions.TimeZones;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

/// <summary>
/// The memory steers the runs of an agent, so a person changes it only under the rule of the chat: holding every role of the
/// agent, or being a full administrator.
/// </summary>
public sealed class AgentMemoryAccessTests : BunitContext, IAsyncLifetime
{
    private readonly AgentMemory _memory = new(filePath: null, TimeProvider.System);
    private readonly AgentServices _services = AgentTestContext.CreateServices(new AgentTestRoot());

    public AgentMemoryAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
    }

    public Task InitializeAsync() => _memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public async Task WhenThePersonLacksARoleOfTheAgent_ThenTheMemoryCannotBeChanged()
    {
        // Arrange
        var editor = CreateEditor(personRoles: [BuiltInRoles.ReaderName], agentRoles: [BuiltInRoles.OperatorName]);
        var entry = _memory.GetSnapshot().Entries[0];

        // Act
        editor.StartAdd();
        var isEditing = editor.IsEditing;
        await editor.DeleteAsync(entry);

        // Assert
        Assert.False(isEditing);
        Assert.Contains(BuiltInRoles.OperatorName, editor.Refusal);
        Assert.Equal(editor.Refusal, editor.Error);
        Assert.Equal(["alerts/freezer"], _memory.GetSnapshot().Entries.Select(item => item.Key));
    }

    [Fact]
    public async Task WhenThePersonLosesARoleWhileEditing_ThenSavingChangesNothing()
    {
        // Arrange
        var permissions = new TestUserPermissions { HeldRoles = [BuiltInRoles.OperatorName] };
        var agent = new FakeAgent(_services) { Roles = [BuiltInRoles.OperatorName] };
        var editor = new AgentMemoryEditor(() => _memory, () => AgentChatAccess.GetRefusal(permissions, agent));
        editor.StartAdd();
        editor.Key = "preferences/quiet-hours";
        editor.Value = "22:00 to 07:00";

        // Act
        permissions.HeldRoles.Clear();
        permissions.HeldRoles.Add(BuiltInRoles.ReaderName);
        await editor.SaveAsync();

        // Assert
        Assert.NotNull(editor.Error);
        Assert.Equal(["alerts/freezer"], _memory.GetSnapshot().Entries.Select(item => item.Key));
    }

    [Fact]
    public async Task WhenThePersonHoldsEveryRoleOfTheAgent_ThenTheMemoryCanBeChanged()
    {
        // Arrange
        var editor = CreateEditor(personRoles: [BuiltInRoles.OperatorName, BuiltInRoles.ReaderName], agentRoles: [BuiltInRoles.ReaderName]);
        editor.StartAdd();
        editor.Key = "preferences/quiet-hours";
        editor.Value = "22:00 to 07:00";

        // Act
        await editor.SaveAsync();

        // Assert
        Assert.Null(editor.Refusal);
        Assert.Null(editor.Error);
        Assert.Equal(2, _memory.GetSnapshot().Entries.Length);
    }

    [Fact]
    public void WhenThePersonLacksARoleOfTheAgent_ThenTheMemoryTabIsReadOnly()
    {
        // Arrange
        var editor = CreateEditor(personRoles: [BuiltInRoles.ReaderName], agentRoles: [BuiltInRoles.OperatorName]);

        // Act
        var tab = Render<AgentMemoryTab>(parameters => parameters.Add(component => component.MemoryEditor, editor));

        // Assert
        Assert.True(tab.Find("[data-testid='agent-memory-add']").HasAttribute("disabled"));
        Assert.Empty(tab.FindAll("button[aria-label^='Edit ']"));
        Assert.Empty(tab.FindAll("button[aria-label^='Delete ']"));
        Assert.Contains(BuiltInRoles.OperatorName, tab.Find("[data-testid='agent-memory-refusal']").TextContent);
    }

    [Fact]
    public void WhenThePersonHoldsEveryRoleOfTheAgent_ThenTheMemoryTabCanBeEdited()
    {
        // Arrange
        var editor = CreateEditor(personRoles: [BuiltInRoles.EverythingName], agentRoles: [BuiltInRoles.OperatorName]);

        // Act
        var tab = Render<AgentMemoryTab>(parameters => parameters.Add(component => component.MemoryEditor, editor));

        // Assert
        Assert.False(tab.Find("[data-testid='agent-memory-add']").HasAttribute("disabled"));
        Assert.Single(tab.FindAll("button[aria-label^='Edit ']"));
        Assert.Empty(tab.FindAll("[data-testid='agent-memory-refusal']"));
    }

    private AgentMemoryEditor CreateEditor(IReadOnlyCollection<string> personRoles, List<string> agentRoles)
    {
        var agent = new FakeAgent(_services) { Roles = agentRoles };
        var permissions = new TestUserPermissions { HeldRoles = [.. personRoles] };
        return new AgentMemoryEditor(() => _memory, () => AgentChatAccess.GetRefusal(permissions, agent));
    }
}
