namespace HomeBlaze.Virtual.Tests;

public class VirtualPropertyValidatorTests
{
    private static readonly VirtualProperty Valid = new() { Expression = "1", DataType = VirtualDataType.Number };

    private static VirtualProperty Expression(string text) => new() { Expression = text, DataType = VirtualDataType.Number };

    [Fact]
    public void WhenEveryEntryIsValid_ThenThereIsNoIssue()
    {
        // Arrange
        var properties = new Dictionary<string, VirtualProperty>
        {
            ["Pv"] = new() { Properties = new Dictionary<string, VirtualProperty>
            {
                ["Power"] = new() { Path = "/Meter/Power", DataType = VirtualDataType.Number, State = new VirtualState { Title = "PV" },
                    Attributes = new Dictionary<string, VirtualProperty> { ["Maximum"] = Valid } },
                ["Double"] = Expression("Power * 2")
            } }
        };

        // Act
        var issues = VirtualPropertyValidator.Validate(properties, "/Solar");

        // Assert
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("")]
    public void WhenANameIsNoIdentifier_ThenItIsAnIssue(string name)
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { [name] = Valid });

        // Assert
        Assert.Single(issues);
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Properties")]
    [InlineData("Status")]
    [InlineData("StatusMessage")]
    [InlineData("IconColor")]
    public void WhenARootNameIsTakenByTheVirtualSubject_ThenItIsAnIssue(string name)
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { [name] = Valid });

        // Assert
        Assert.Equal(name, Assert.Single(issues).Path);
    }

    [Fact]
    public void WhenAnEntryHasTwoSources_ThenItIsAnIssue()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty>
        {
            ["A"] = new() { Expression = "1", Path = "/x/y", DataType = VirtualDataType.Number }
        });

        // Assert
        Assert.Equal("An entry has exactly one of expression, path or properties.", Assert.Single(issues).Message);
    }

    [Fact]
    public void WhenAPropertyHasNoDataType_ThenItIsAnIssue()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { ["A"] = new() { Expression = "1" } });

        // Assert
        Assert.Equal("A data type is required.", Assert.Single(issues).Message);
    }

    [Fact]
    public void WhenANestedSubjectHasADataType_ThenItIsAnIssue()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty>
        {
            ["A"] = new() { Properties = new Dictionary<string, VirtualProperty>(), DataType = VirtualDataType.Number }
        });

        // Assert
        Assert.Equal("A nested subject has no data type, state or attributes.", Assert.Single(issues).Message);
    }

    [Fact]
    public void WhenAnAttributeHoldsProperties_ThenItIsAnIssueAtItsPath()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty>
        {
            ["A"] = Valid with { Attributes = new Dictionary<string, VirtualProperty> { ["Max"] = new() { Properties = new Dictionary<string, VirtualProperty>() } } }
        });

        // Assert
        var issue = Assert.Single(issues);
        Assert.Equal("A@Max", issue.Path);
        Assert.Equal("An attribute cannot hold properties.", issue.Message);
    }

    [Fact]
    public void WhenAnExpressionDoesNotParse_ThenTheIssueHasTheMessageOfTheParser()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { ["A"] = Expression("(1") });

        // Assert
        Assert.Contains("at position", Assert.Single(issues).Message);
    }

    [Fact]
    public void WhenAnEntryIsInvalid_ThenItsChildrenAreNotChecked()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty>
        {
            ["1bad"] = new() { Properties = new Dictionary<string, VirtualProperty> { ["2bad"] = Valid } }
        });

        // Assert
        Assert.Equal("1bad", Assert.Single(issues).Path);
    }

    [Fact]
    public void WhenSiblingsReadEachOther_ThenBothAreIssues()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { ["A"] = Expression("B + 1"), ["B"] = Expression("A + 1") });

        // Assert
        Assert.Equal(["A", "B"], issues.Select(issue => issue.Path).Order());
        Assert.All(issues, issue => Assert.Equal("The entry reads itself through other entries.", issue.Message));
    }

    [Fact]
    public void WhenAnEntryReadsItselfThroughTheAbsolutePathOfTheSubject_ThenItIsAnIssue()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { ["A"] = Expression("/Solar/A + 1") }, "/Solar");

        // Assert
        Assert.Equal("A", Assert.Single(issues).Path);
    }

    [Fact]
    public void WhenANestedEntryReadsUpwardsIntoACycle_ThenTheCycleIsFound()
    {
        // Arrange
        var properties = new Dictionary<string, VirtualProperty>
        {
            ["Pv"] = new() { Properties = new Dictionary<string, VirtualProperty> { ["A"] = Expression("../B") } },
            ["B"] = Expression("Pv/A")
        };

        // Act
        var issues = VirtualPropertyValidator.Validate(properties);

        // Assert
        Assert.Equal(["B", "Pv/A"], issues.Select(issue => issue.Path).Order());
    }

    [Theory]
    [InlineData("/Solar/Pv/A")]
    [InlineData("./A")]
    [InlineData("../Pv/A")]
    public void WhenANestedEntryReadsItselfThroughAnotherSpellingOfItsPath_ThenItIsAnIssue(string path)
    {
        // Arrange
        var properties = new Dictionary<string, VirtualProperty>
        {
            ["Pv"] = new() { Properties = new Dictionary<string, VirtualProperty> { ["A"] = Expression(path + " + 1") } }
        };

        // Act
        var issues = VirtualPropertyValidator.Validate(properties, "/Solar");

        // Assert
        Assert.Equal("Pv/A", Assert.Single(issues).Path);
    }

    [Theory]
    [InlineData("/Other/A")]
    [InlineData("/SolarPanel/A")]
    [InlineData("../A")]
    public void WhenAnEntryReadsAPathOutsideTheSubject_ThenItIsNoCycle(string path)
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty> { ["A"] = Expression(path + " + 1") }, "/Solar");

        // Assert
        Assert.Empty(issues);
    }

    [Fact]
    public void WhenAnAttributeReadsItself_ThenItIsAnIssue()
    {
        // Act
        var issues = VirtualPropertyValidator.Validate(new Dictionary<string, VirtualProperty>
        {
            ["A"] = Valid with { Attributes = new Dictionary<string, VirtualProperty> { ["Max"] = Expression("A@Max + 1") } }
        });

        // Assert
        Assert.Equal("A@Max", Assert.Single(issues).Path);
    }

    [Fact]
    public void WhenCyclicEntriesAreAskedFor_ThenTheirPathsAreGiven()
    {
        // Act
        var cyclic = VirtualPropertyValidator.FindCyclicEntries(
            new Dictionary<string, VirtualProperty> { ["A"] = Expression("B"), ["B"] = Expression("A"), ["C"] = Expression("A") }, null);

        // Assert
        Assert.Equal(["A", "B"], cyclic.Order());
    }
}
