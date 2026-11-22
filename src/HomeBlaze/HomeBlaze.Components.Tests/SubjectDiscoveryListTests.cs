using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectDiscoveryListTests
{
    private static readonly SubjectTypeDescriptor Hue = new(typeof(string), "Hue Bridge", null, "Lighting", "Hub");
    private static readonly SubjectTypeDescriptor Shelly = new(typeof(int), "Shelly device", null, "Switches", "Power");

    private static DiscoveredSubject Found(SubjectTypeDescriptor descriptor, string name) =>
        new(descriptor.Type, new FoundDevice(name), null);

    [Fact]
    public void WhenResultsArriveInAnyOrder_ThenRowsAreOrderedByTypeAndThenName()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.Add(Found(Shelly, "shellyplus1pm-b"), Shelly);
        list.Add(Found(Hue, "Zeta"), Hue);
        list.Add(Found(Shelly, "shellyplus1pm-a"), Shelly);

        // Assert
        Assert.Equal(["Zeta", "shellyplus1pm-a", "shellyplus1pm-b"], list.Rows.Select(row => row.Discovered.Result.DisplayName));
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

    private sealed record FoundDevice(string DisplayName, string? KeyOverride = null) : IDiscoveryResult
    {
        public string Key => KeyOverride ?? DisplayName;

        public string? Detail => null;
    }
}
