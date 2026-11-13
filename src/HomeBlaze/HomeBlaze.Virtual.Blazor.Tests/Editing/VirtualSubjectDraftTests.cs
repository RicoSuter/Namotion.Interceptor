using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Virtual.Blazor.Editing;

namespace HomeBlaze.Virtual.Blazor.Tests.Editing;

public class VirtualSubjectDraftTests
{
    [Fact]
    public void WhenADraftIsCreatedFromPropertiesAndConvertedBack_ThenThePropertiesAreTheSame()
    {
        // Arrange
        var properties = new Dictionary<string, VirtualProperty>
        {
            ["Pv"] = new()
            {
                Properties = new Dictionary<string, VirtualProperty>
                {
                    ["Power"] = new()
                    {
                        Path = "/Meter/Power",
                        DataType = VirtualDataType.Number,
                        State = new VirtualState { Title = "PV", Unit = StateUnit.Watt, IsCumulative = true },
                        Attributes = new Dictionary<string, VirtualProperty> { ["Maximum"] = new() { Expression = "10", DataType = VirtualDataType.Number } }
                    }
                }
            }
        };

        // Act
        var result = VirtualSubjectDraft.From("Solar", properties).ToProperties();

        // Assert
        var power = result!["Pv"].Properties!["Power"];
        Assert.Equal("/Meter/Power", power.Path);
        Assert.Equal(VirtualDataType.Number, power.DataType);
        Assert.Equal(new VirtualState { Title = "PV", Unit = StateUnit.Watt, IsCumulative = true }, power.State);
        Assert.Equal("10", power.Attributes!["Maximum"].Expression);
    }

    [Fact]
    public void WhenAPropertyIsAddedToASelectedProperty_ThenItGoesToItsSubject()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        var group = draft.AddSubject(null);
        var first = draft.AddProperty(group);

        // Act
        var second = draft.AddProperty(first);

        // Assert
        Assert.Same(group, second.Parent);
        Assert.Equal(["Property", "Property2"], group.Properties.Select(entry => entry.Name));
        Assert.Equal("Group/Property2", second.GetPath());
    }

    [Fact]
    public void WhenAnAttributeIsAdded_ThenItsPathUsesTheAttributeSeparator()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        var property = draft.AddProperty(null);

        // Act
        var attribute = draft.AddAttribute(property);

        // Assert
        Assert.Equal("Property@Minimum", attribute.GetPath());
    }

    [Fact]
    public void WhenTwoEntriesShareAName_ThenTheDraftReportsIt()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        draft.AddProperty(null);
        var second = draft.AddProperty(null);
        second.Name = "Property";

        // Act
        var issues = draft.Validate();

        // Assert
        Assert.Contains(issues, issue => issue.Path == "Property" && issue.Message == "The name is used twice at this level.");
    }

    [Fact]
    public void WhenAnEntryIsRemoved_ThenItsChildrenGoWithIt()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        var group = draft.AddSubject(null);
        draft.AddProperty(group);

        // Act
        draft.Remove(group);

        // Assert
        Assert.Empty(draft.Properties);
        Assert.Null(draft.ToProperties());
    }

    [Fact]
    public void WhenAnExpressionIsEmpty_ThenTheDraftReportsTheIssueOfTheValidator()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        draft.AddProperty(null);

        // Act
        var issues = draft.Validate();

        // Assert
        Assert.Single(issues, issue => issue.Path == "Property");
    }

    [Fact]
    public void WhenAStateTargetIsTakenOver_ThenTheEntryBecomesStateWithItsTypeUnitAndTitle()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        var entry = draft.AddProperty(null);
        entry.IsState = false;
        entry.DataType = VirtualDataType.Text;
        entry.Title = " ";

        // Act
        entry.TakeOver(typeof(int), new StateMetadata { Title = "Target speed", Unit = StateUnit.Hertz });

        // Assert
        Assert.True(entry.IsState);
        Assert.Equal(VirtualDataType.Number, entry.DataType);
        Assert.Equal(StateUnit.Hertz, entry.Unit);
        Assert.Equal("Target speed", entry.Title);
    }

    [Fact]
    public void WhenATargetWithoutStateIsTakenOver_ThenTheUnitIsClearedAndTheTitleKept()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        var entry = draft.AddProperty(null);
        entry.Unit = StateUnit.Watt;
        entry.Title = "Mine";

        // Act
        entry.TakeOver(typeof(bool), state: null);

        // Assert
        Assert.Equal(VirtualDataType.Boolean, entry.DataType);
        Assert.Null(entry.Unit);
        Assert.Equal("Mine", entry.Title);
    }

    [Fact]
    public void WhenTheUnitIsDefault_ThenItIsSavedAsNone()
    {
        // Arrange
        var draft = VirtualSubjectDraft.From(null, null);
        var entry = draft.AddProperty(null);
        entry.Expression = "1";
        entry.Unit = StateUnit.Default;

        // Act
        var result = draft.ToProperties();

        // Assert
        Assert.Null(result!["Property"].State!.Unit);
    }
}
