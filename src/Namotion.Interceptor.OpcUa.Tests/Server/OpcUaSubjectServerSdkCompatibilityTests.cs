using System.Reflection;
using Opc.Ua.Bindings;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// Guards against OPC UA SDK internal changes that would break our
/// reflection-based workarounds for SDK disposal bugs (Fix 14).
/// If any of these tests fail after an SDK upgrade, the corresponding
/// workaround in OpcUaSubjectServer needs to be updated.
/// TODO: Remove when https://github.com/OPCFoundation/UA-.NETStandard/pull/3560 is released.
/// </summary>
public class OpcUaSubjectServerSdkCompatibilityTests
{
    [Fact]
    public void TcpTransportListener_HasPrivateCallbackField()
    {
        // OpcUaSubjectServer nulls m_callback via reflection after disposing
        // transport listeners to break the GC retention chain:
        // Socket → Channel → Listener → m_callback → SessionEndpoint → Server.
        var field = typeof(TcpTransportListener)
            .GetField("m_callback", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
    }
}
