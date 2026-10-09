using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Serialization;

public enum TestMode
{
    Off,
    Heating
}

[InterceptorSubject]
public partial class TestModeDevice
{
    public partial TestMode Mode { get; set; }
}

public class EnumValueFlowTests
{
    [Fact]
    public void WhenEnumValueIsSerializedAsName_ThenApplyConvertsItBack()
    {
        // Arrange
        var serverDevice = new TestModeDevice(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry())
        {
            Mode = TestMode.Heating
        };
        var clientDevice = new TestModeDevice(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());

        // The serializer writes enums as camel case names, so the value arrives as a JSON string.
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.Serialize(SubjectUpdate.CreateCompleteUpdate(serverDevice, []));
        var update = serializer.Deserialize<SubjectUpdate>(bytes);

        // Act
        clientDevice.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(TestMode.Heating, clientDevice.Mode);
    }
}
