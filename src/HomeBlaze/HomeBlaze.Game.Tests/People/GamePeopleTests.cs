using HomeBlaze.Game.People;

namespace HomeBlaze.Game.Tests.People;

public class GamePeopleTests
{
    [Fact]
    public void WhenTheDefaultIsAskedTwiceForOneClient_ThenItIsTheSamePerson()
    {
        // Act
        var first = GamePeople.GetDefault("tab-1");
        var second = GamePeople.GetDefault("tab-1");

        // Assert
        Assert.Equal(first, second);
        Assert.Contains(first, GamePeople.Names);
    }

    [Theory]
    [InlineData("male-casual", "Male, casual")]
    [InlineData("female-tank-top", "Female, tank top")]
    public void WhenAPersonIsLabeled_ThenTheLabelIsReadable(string person, string label)
    {
        // Act & Assert
        Assert.Equal(label, GamePeople.GetLabel(person));
    }

    [Fact]
    public void WhenManyClientsJoin_ThenEveryPersonIsUsed()
    {
        // Act
        var used = Enumerable.Range(0, 200).Select(index => GamePeople.GetDefault($"tab-{index}")).ToHashSet();

        // Assert
        Assert.Equal(GamePeople.Names.Length, used.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("robot")]
    [InlineData("../male-casual")]
    public void WhenTheStoredPersonIsNotOneOfThePeople_ThenTheDefaultIsUsed(string? stored)
    {
        // Act
        var resolved = GamePeople.Resolve(stored, "tab-1");

        // Assert
        Assert.Equal(GamePeople.GetDefault("tab-1"), resolved);
    }

    [Fact]
    public void WhenTheStoredPersonIsOneOfThePeople_ThenItIsUsed()
    {
        // Act
        var resolved = GamePeople.Resolve("female-tank-top", "tab-1");

        // Assert
        Assert.Equal("female-tank-top", resolved);
    }
}
