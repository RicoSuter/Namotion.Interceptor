using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace HomeBlaze.Abstractions.Tests;

public class DiscoveredItemTests
{
    [Fact]
    public void WhenItemsAreSortedInDisplayOrder_ThenTheNameIgnoringCaseAndThenTheDetailDecide()
    {
        // Arrange
        List<DiscoveredItem> items =
        [
            new("living room", "Sonos One, 192.0.2.102"),
            new("Kitchen", null),
            new("Living Room", "Sonos Sub, 192.0.2.101"),
            new("Living Room", "Sonos Arc, 192.0.2.103")
        ];

        // Act
        items.Sort(DiscoveredItem.DisplayOrder);

        // Assert
        Assert.Equal(
            [
                new DiscoveredItem("Kitchen", null),
                new DiscoveredItem("Living Room", "Sonos Arc, 192.0.2.103"),
                new DiscoveredItem("living room", "Sonos One, 192.0.2.102"),
                new DiscoveredItem("Living Room", "Sonos Sub, 192.0.2.101")
            ],
            items);
    }
}
