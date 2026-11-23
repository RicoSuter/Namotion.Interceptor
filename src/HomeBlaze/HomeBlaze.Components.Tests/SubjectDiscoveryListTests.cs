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
    public void WhenADiscoveryFails_ThenItsTypeIsNamedInAFailureLine()
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

    private sealed record FoundDevice(string DisplayName, string? KeyOverride = null) : IDiscoveryResult
    {
        public string Key => KeyOverride ?? DisplayName;

        public string? Detail => null;
    }
}
