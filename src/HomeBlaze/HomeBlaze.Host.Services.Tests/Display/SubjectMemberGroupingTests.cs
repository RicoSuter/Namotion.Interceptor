using HomeBlaze.Host.Services.Display;

namespace HomeBlaze.Host.Services.Tests.Display;

public class SubjectMemberGroupingTests
{
    private sealed record Member(string Name, string? Category, int Position);

    private static SubjectMemberGroups<Member, Member> Group(Member[] values, Member[] methods)
    {
        return SubjectMemberGrouping.Group(
            values, static value => value.Category, static value => value.Position,
            methods, static method => method.Category, static method => method.Position);
    }

    private static string[] Names(IReadOnlyList<Member> members) => members.Select(member => member.Name).ToArray();

    [Fact]
    public void WhenNoMemberHasACategory_ThenAllMembersAreReturnedAsGivenWithoutCategories()
    {
        // Arrange
        Member[] values = [new("Status", null, 1), new("Speed", null, 2)];
        Member[] methods = [new("Stop", null, 1), new("Reset", null, 2)];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Same(values, groups.UncategorizedValues);
        Assert.Same(methods, groups.UncategorizedMethods);
        Assert.Empty(groups.Categories);
    }

    [Fact]
    public void WhenSomeMembersHaveACategory_ThenTheOthersStayOutsideTheCategories()
    {
        // Arrange
        Member[] values = [new("Status", null, 1), new("Target", "Speed", 2), new("Temperature", null, 3)];
        Member[] methods = [new("Set Speed", "Speed", 1), new("Stop", null, 2)];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Equal(["Status", "Temperature"], Names(groups.UncategorizedValues));
        Assert.Equal(["Stop"], Names(groups.UncategorizedMethods));

        var category = Assert.Single(groups.Categories);
        Assert.Equal("Speed", category.Name);
        Assert.Equal(["Target"], Names(category.Values));
        Assert.Equal(["Set Speed"], Names(category.Methods));
    }

    [Fact]
    public void WhenCategoriesHaveValues_ThenTheyAreOrderedByTheirLowestValuePosition()
    {
        // Arrange
        Member[] values =
        [
            new("Bass", "Sound", 20),
            new("Volume", "Volume", 30),
            new("Is Muted", "Volume", 5),
            new("Playback State", "Playback", 10)
        ];

        // A method position does not place a category that has values.
        Member[] methods = [new("Set Bass", "Sound", 1)];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Equal(["Volume", "Playback", "Sound"], groups.Categories.Select(category => category.Name));
    }

    [Fact]
    public void WhenACategoryHasOnlyMethods_ThenItComesAfterTheCategoriesWithValues()
    {
        // Arrange
        Member[] values = [new("Volume", "Volume", 100), new("Bass", "Sound", 200)];
        Member[] methods =
        [
            new("Join Group", "Group", 50),
            new("Reboot", "Maintenance", 1),
            new("Leave Group", "Group", 2),
            new("Set Volume", "Volume", 90)
        ];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Equal(["Volume", "Sound", "Maintenance", "Group"], groups.Categories.Select(category => category.Name));
    }

    [Fact]
    public void WhenTheValuesOfACategoryHaveTheDefaultPosition_ThenItStillComesBeforeACategoryWithOnlyMethods()
    {
        // Arrange
        // Neither the positions nor the names put "Sound" first, only that it has a value.
        Member[] values = [new("Bass", "Sound", int.MaxValue)];
        Member[] methods = [new("Join Group", "Group", 1)];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Equal(["Sound", "Group"], groups.Categories.Select(category => category.Name));
    }

    [Fact]
    public void WhenEveryValueHasACategory_ThenNoValueStaysOutsideTheCategories()
    {
        // Arrange
        Member[] values = [new("Volume", "Volume", 1), new("Bass", "Sound", 2)];

        // Act
        var groups = Group(values, []);

        // Assert
        Assert.Empty(groups.UncategorizedValues);
    }

    [Fact]
    public void WhenCategoriesShareAPosition_ThenTheyAreOrderedByName()
    {
        // Arrange
        Member[] values = [new("Second", "b", 1), new("First", "a", 1), new("Upper", "B", 1)];
        Member[] methods = [new("Last", "z", 7), new("Middle", "y", 7)];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Equal(["B", "a", "b", "y", "z"], groups.Categories.Select(category => category.Name));
    }

    [Fact]
    public void WhenCategoryNamesDifferByCase_ThenTheyAreSeparateCategories()
    {
        // Arrange
        Member[] values = [new("First", "Volume", 1), new("Second", "volume", 2)];

        // Act
        var groups = Group(values, []);

        // Assert
        Assert.Equal(["Volume", "volume"], groups.Categories.Select(category => category.Name));
    }

    [Fact]
    public void WhenACategoryHasSeveralMembers_ThenTheyKeepTheOrderTheyWereGivenIn()
    {
        // Arrange
        // The lists arrive in display order, which for members at the same position is not the order of their names.
        Member[] values =
        [
            new("Playback State", "Playback", 10),
            new("Volume", "Volume", 11),
            new("Shuffle", "Playback", 12),
            new("Repeat", "Playback", 12),
            new("Is Playing", "Playback", 140)
        ];
        Member[] methods =
        [
            new("Play", "Playback", 1),
            new("Set Volume", "Volume", 2),
            new("Pause", "Playback", 3),
            new("Next", "Playback", 3)
        ];

        // Act
        var groups = Group(values, methods);

        // Assert
        var playback = groups.Categories[0];
        Assert.Equal("Playback", playback.Name);
        Assert.Equal(["Playback State", "Shuffle", "Repeat", "Is Playing"], Names(playback.Values));
        Assert.Equal(["Play", "Pause", "Next"], Names(playback.Methods));
    }

    [Fact]
    public void WhenOnlyMethodsHaveACategory_ThenTheValuesAreReturnedAsGiven()
    {
        // Arrange
        Member[] values = [new("Status", null, 1)];
        Member[] methods = [new("Stop", "Control", 1), new("Reset", null, 2)];

        // Act
        var groups = Group(values, methods);

        // Assert
        Assert.Same(values, groups.UncategorizedValues);
        Assert.Equal(["Reset"], Names(groups.UncategorizedMethods));

        var category = Assert.Single(groups.Categories);
        Assert.Empty(category.Values);
        Assert.Equal(["Stop"], Names(category.Methods));
    }
}
