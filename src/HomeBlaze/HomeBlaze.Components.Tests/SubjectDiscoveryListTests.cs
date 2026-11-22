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
        list.Add(Found(Hue, "Bridge"), Hue);
        list.Add(Found(Shelly, "shellyplus1pm-a"), Shelly);

        // Assert
        Assert.Equal(["Bridge", "shellyplus1pm-a", "shellyplus1pm-b"], list.Rows.Select(row => row.Discovered.Result.DisplayName));
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

    private sealed record FoundDevice(string DisplayName) : IDiscoveryResult
    {
        public string Key => DisplayName;

        public string? Detail => null;
    }
}
