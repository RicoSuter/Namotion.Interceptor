using Moq;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectPropertyChangeMergeExtensionsTests
{
    [Fact]
    public void WhenIncomingCarriesTheHigherRevision_ThenSurvivorTakesIncomingNewValueAndKeptOldValue()
    {
        // Arrange
        var property = CreateProperty("Value");
        var kept = CreateChange(property, 1, revision: 3);
        var incoming = CreateChange(property, 5, revision: 7);

        // Act
        var survivor = kept.MergeByRevision(incoming);

        // Assert
        Assert.Equal(0, survivor.GetOldValue<int>());
        Assert.Equal(5, survivor.GetNewValue<int>());
        Assert.Equal(7, survivor.Revision);
    }

    [Fact]
    public void WhenKeptCarriesTheHigherRevision_ThenSurvivorTakesKeptNewValueAndIncomingOldValue()
    {
        // Arrange
        var property = CreateProperty("Value");
        var kept = CreateChange(property, 5, revision: 7);
        var incoming = CreateChange(property, 1, revision: 3);

        // Act
        var survivor = kept.MergeByRevision(incoming);

        // Assert
        Assert.Equal(0, survivor.GetOldValue<int>());
        Assert.Equal(5, survivor.GetNewValue<int>());
        Assert.Equal(7, survivor.Revision);
    }

    /// <summary>
    /// Two changes to one property cannot share a revision in production, since the terminal advances the
    /// subject's counter per write. The tie is reachable only when both carry none, which means a contract
    /// violation upstream; the later arrival wins so the rule matches the other collapse sites.
    /// </summary>
    [Fact]
    public void WhenRevisionsAreEqual_ThenTheLaterArrivalSuppliesTheNewValue()
    {
        // Arrange
        var property = CreateProperty("Value");
        var kept = CreateChange(property, 1, revision: 0);
        var incoming = CreateChange(property, 5, revision: 0);

        // Act
        var survivor = kept.MergeByRevision(incoming);

        // Assert
        Assert.Equal(0, survivor.GetOldValue<int>());
        Assert.Equal(5, survivor.GetNewValue<int>());
        Assert.Equal(0, survivor.Revision);
    }

    private static PropertyReference CreateProperty(string name)
    {
        return new PropertyReference(new Mock<IInterceptorSubject>().Object, name);
    }

    private static SubjectPropertyChange CreateChange(PropertyReference property, int newValue, long revision)
    {
        return SubjectPropertyChange.Create(
            property,
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            newValue - 1,
            newValue,
            revision);
    }
}
