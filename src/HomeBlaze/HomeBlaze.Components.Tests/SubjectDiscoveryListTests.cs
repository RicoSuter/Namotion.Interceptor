using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using Moq;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectDiscoveryListTests
{
    private static readonly SubjectTypeDescriptor Hue = new(typeof(string), "Hue Bridge", null, "Lighting", "Hub");
    private static readonly SubjectTypeDescriptor Shelly = new(typeof(int), "Shelly device", null, "Switches", "Power");

    private static DiscoveredSubject Found(SubjectTypeDescriptor descriptor, string name, IInterceptorSubject? existingSubject = null) =>
        new(descriptor.Type, new FoundDevice(name), existingSubject);

    private static IEnumerable<string> Names(SubjectDiscoveryList list) =>
        list.Rows.Select(row => row.Discovered.Result.DisplayName);

    [Fact]
    public void WhenResultsArriveInAnyOrder_ThenRowsAreOrderedByTypeAndThenName()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.Add(Found(Shelly, "shellyplus1pm-b"), Shelly);
        list.Add(Found(Hue, "Zeta"), Hue);
        list.Add(Found(Shelly, "shellyplus1pm-a"), Shelly);
        list.Add(Found(Hue, "Hue Bridge"), Hue);

        // Assert
        Assert.Equal(["Hue Bridge", "Zeta", "shellyplus1pm-a", "shellyplus1pm-b"], Names(list));
    }

    [Fact]
    public void WhenSomeDevicesAreAlreadyAdded_ThenTheyAreListedAfterTheOthers()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var existingSubject = Mock.Of<IInterceptorSubject>();

        // Act
        list.Add(Found(Hue, "Alpha", existingSubject), Hue);
        list.Add(Found(Shelly, "Delta"), Shelly);
        list.Add(Found(Shelly, "Beta", existingSubject), Shelly);
        list.Add(Found(Hue, "Gamma"), Hue);

        // Assert
        Assert.Equal(["Gamma", "Delta", "Alpha", "Beta"], Names(list));
    }

    [Fact]
    public void WhenARowIsMarkedAdded_ThenItHasTheSubjectAndMovesAfterTheRowsNotAdded()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var existingSubject = Mock.Of<IInterceptorSubject>();
        var createdSubject = Mock.Of<IInterceptorSubject>();
        list.Add(Found(Hue, "Alpha"), Hue);
        list.Add(Found(Shelly, "Beta"), Shelly);
        list.Add(Found(Shelly, "Zeta", existingSubject), Shelly);
        list.Add(Found(Hue, "Gamma"), Hue);

        // Act
        list.MarkAdded(Found(Hue, "Alpha"), createdSubject);

        // Assert
        Assert.Equal(["Gamma", "Beta", "Alpha", "Zeta"], Names(list));
        Assert.Same(createdSubject, list.Rows[2].Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenNoRowHasTheTypeAndKey_ThenMarkAddedChangesNothing()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Alpha"), Hue);

        // Act
        list.MarkAdded(Found(Shelly, "Alpha"), Mock.Of<IInterceptorSubject>());

        // Assert
        Assert.Null(Assert.Single(list.Rows).Discovered.ExistingSubject);
    }

    [Fact]
    public void WhenNamesDifferOnlyInCaseOrKey_ThenTheOrderIsTheSameForAnyArrivalOrder()
    {
        // Arrange
        var upperCase = Found(Hue, "BRIDGE");
        var lowerCase = Found(Hue, "bridge");
        var firstKey = new DiscoveredSubject(Hue.Type, new FoundDevice("Same", "a"), null);
        var secondKey = new DiscoveredSubject(Hue.Type, new FoundDevice("Same", "b"), null);
        var first = new SubjectDiscoveryList();
        var second = new SubjectDiscoveryList();

        // Act
        foreach (var discovered in new[] { upperCase, lowerCase, secondKey, firstKey })
        {
            first.Add(discovered, Hue);
        }

        foreach (var discovered in new[] { firstKey, secondKey, lowerCase, upperCase })
        {
            second.Add(discovered, Hue);
        }

        // Assert
        Assert.Equal(first.Rows.Select(row => row.Discovered.Result.Key), second.Rows.Select(row => row.Discovered.Result.Key));
        Assert.Equal(["a", "b"], first.Rows.Select(row => row.Discovered.Result.Key).Where(key => key is "a" or "b"));
        Assert.Equal(["BRIDGE", "bridge"], first.Rows.Select(row => row.Discovered.Result.Key).Where(key => key.Equals("bridge", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void WhenADiscovererFails_ThenItsTypeIsNamedInAFailureLine()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.AddFailure(Hue);

        // Assert
        Assert.Equal(["Hue Bridge: search failed"], list.Failures);
    }

    [Fact]
    public void WhenTheListIsCleared_ThenRowsAndFailuresAreGone()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Bridge"), Hue);
        list.AddFailure(Shelly);

        // Act
        list.Clear();

        // Assert
        Assert.Empty(list.Rows);
        Assert.Empty(list.Failures);
    }

    [Fact]
    public void WhenARowIsToggled_ThenOnlyItIsExpandedAndStaysSoWhileResultsArrive()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Bridge"), Hue);
        list.Add(Found(Shelly, "Bridge"), Shelly);
        var hueRow = list.Rows.Single(row => row.Descriptor == Hue);
        var shellyRow = list.Rows.Single(row => row.Descriptor == Shelly);

        // Act
        list.ToggleExpanded(hueRow);
        list.Add(Found(Hue, "Another bridge"), Hue);

        // Assert
        Assert.True(list.IsExpanded(hueRow));
        Assert.False(list.IsExpanded(shellyRow));
        Assert.True(list.IsExpanded(list.Rows.Single(row => row.Descriptor == Hue && row.Discovered.Result.DisplayName == "Bridge")));
    }

    [Fact]
    public void WhenAnExpandedRowIsToggledAgain_ThenItIsCollapsed()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Bridge"), Hue);
        var row = Assert.Single(list.Rows);
        list.ToggleExpanded(row);

        // Act
        list.ToggleExpanded(row);

        // Assert
        Assert.False(list.IsExpanded(row));
    }

    [Fact]
    public void WhenAnExpandedRowIsMarkedAdded_ThenItStaysExpanded()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        var found = Found(Hue, "Alpha");
        list.Add(found, Hue);
        list.Add(Found(Hue, "Beta"), Hue);
        list.ToggleExpanded(list.Rows[0]);

        // Act
        list.MarkAdded(found, Mock.Of<IInterceptorSubject>());

        // Assert
        Assert.Equal(["Beta", "Alpha"], Names(list));
        Assert.True(list.IsExpanded(list.Rows[1]));
        Assert.False(list.IsExpanded(list.Rows[0]));
    }

    [Fact]
    public void WhenTheListIsClearedForANewScan_ThenTheSameDeviceIsCollapsedAgain()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Bridge"), Hue);
        list.ToggleExpanded(Assert.Single(list.Rows));

        // Act
        list.Clear();
        list.Add(Found(Hue, "Bridge"), Hue);

        // Assert
        Assert.False(list.IsExpanded(Assert.Single(list.Rows)));
    }

    [Theory]
    [InlineData(1, false, "Show 1 included item")]
    [InlineData(10, false, "Show 10 included items")]
    [InlineData(10, true, "Hide 10 included items")]
    public void WhenARowHasIncludedItems_ThenItsExpanderIsLabeledWithTheirCount(int itemCount, bool isExpanded, string expectedLabel)
    {
        // Act & Assert
        Assert.Equal(expectedLabel, SubjectDiscoveryList.GetExpanderLabel(itemCount, isExpanded));
    }

    [Fact]
    public void WhenAResultDoesNotListIncludedItems_ThenItIncludesNone()
    {
        // Arrange
        IDiscoveryResult result = new FoundDevice("Bridge");
        IDiscoveryResult other = new FoundDevice("Other");

        // Act & Assert - the same empty list for every result, so the default allocates nothing.
        Assert.Empty(result.IncludedItems);
        Assert.Same(result.IncludedItems, other.IncludedItems);
    }

    private sealed record FoundDevice(string DisplayName, string? KeyOverride = null) : IDiscoveryResult
    {
        public string Key => KeyOverride ?? DisplayName;

        public string? Detail => null;
    }
}
