using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// A client writes into the node tree before the subject sees it, so an applied value is already at the
/// destination and must supersede an older local commit. Picking the other rule is silent: the node keeps
/// serving a value the model has moved past, and no transport test notices. Reads the rule back off a
/// constructed processor, so inlining a different value at the construction site fails this too.
/// </summary>
public class OpcUaServerDeliveryRuleTests
{
    [Fact]
    public void WhenTheServerCreatesItsProcessor_ThenItSelectsTheServerRule()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var subject = new DeliveryRuleTestRoot(context);
        var server = new OpcUaSubjectServer(subject, new OpcUaServerConfiguration(), NullLogger.Instance);

        // Act
        using var processor = server.CreateChangeQueueProcessor();

        // Assert
        Assert.Equal(ChangeDeliveryRule.SourceValuesAreSettled, processor.DeliveryRule);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class DeliveryRuleTestRoot
{
    public partial string? Name { get; set; }
}
