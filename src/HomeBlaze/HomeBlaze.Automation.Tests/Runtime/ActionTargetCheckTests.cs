using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests.Runtime;

public class ActionTargetCheckTests
{
    private readonly TestGraph _graph = new();
    private readonly AutomationRule _rule = new();

    public ActionTargetCheckTests() => _graph.Add("Blinds", new TestBlinds());

    private ActionTargetCheck Create(IAutomationPermissions permissions) => new(_graph.Resolver, permissions, _ => _rule);

    private static IAutomationPermissions AllowAll => new AllowAllAutomationAuthorizer().GetPermissions([]);

    [Theory]
    [InlineData("/Nothing/Close")]
    [InlineData("./Close")]
    public void WhenATargetDoesNotResolve_ThenItIsNotCheckableYet(string path)
    {
        // Act
        var error = Create(new DenyingPermissions()).Check("r", Invoke(path));

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenResolvedTargetsAreWhatAnAutomationNeverDoes_ThenEachHasItsError()
    {
        // Arrange
        var check = Create(AllowAll);

        // Act
        var query = check.Check("r", Invoke("/Blinds/GetPosition"));
        var confirmation = check.Check("r", Invoke("/Blinds/Calibrate"));
        var property = check.Check("r", Invoke("/Blinds/Position"));
        var configuration = check.Check("r", Set("/Blinds/Label", "x"));
        var readOnly = check.Check("r", Set("/Blinds/Level", "1"));

        // Assert
        Assert.Equal("/Blinds/GetPosition is a query, not an operation", query);
        Assert.Equal("/Blinds/Calibrate requires confirmation by a person and cannot be called by an automation", confirmation);
        Assert.Equal("No operation at /Blinds/Position", property);
        Assert.Equal("/Blinds/Label is configuration, which an automation cannot change", configuration);
        Assert.Equal("/Blinds/Level is read-only", readOnly);
    }

    [Fact]
    public void WhenThePermissionsDenyAResolvedTarget_ThenTheDenialIsTheError()
    {
        // Arrange
        var check = Create(new DenyingPermissions());

        // Act
        var call = check.Check("r", Invoke("/Blinds/Close"));
        var write = check.Check("r", Set("/Blinds/Position", "1"));

        // Assert
        Assert.Equal("/Blinds/Close: the automation may not call Close", call);
        Assert.Equal("/Blinds/Position: the automation may not write Position", write);
    }

    [Fact]
    public void WhenTheRolesCannotBeUsed_ThenOnlyWhatAnAutomationNeverDoesIsAnError()
    {
        // Arrange
        var check = Create(new DenyingPermissions("the roles of the automation cannot be used: Role 'Ghost' is not defined."));

        // Act
        var call = check.Check("r", Invoke("/Blinds/Close"));
        var confirmation = check.Check("r", Invoke("/Blinds/Calibrate"));

        // Assert
        Assert.Null(call);
        Assert.Equal("/Blinds/Calibrate requires confirmation by a person and cannot be called by an automation", confirmation);
    }
}
