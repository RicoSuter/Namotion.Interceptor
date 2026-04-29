using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

[InterceptorSubject]
public partial class CleanupRecursionRoot
{
    [Path("opc", "Child")]
    public partial CleanupRecursionChild? Child { get; set; }
}

[InterceptorSubject]
public partial class CleanupRecursionChild
{
    public CleanupRecursionChild()
    {
        Number_Unit = "kg";
        Number_Unit_Source = "scale";
    }

    [Path("opc", "Number")]
    public partial decimal Number { get; set; }

    [PropertyAttribute(nameof(Number), "Unit")]
    [Path("opc", "Unit")]
    public partial string Number_Unit { get; set; }

    [PropertyAttribute(nameof(Number_Unit), "Source")]
    [Path("opc", "Source")]
    public partial string Number_Unit_Source { get; set; }
}

/// <summary>
/// Verifies that the OPC UA server recurses through nested attributes
/// (attribute-of-attribute) when a subject is detached, clearing the
/// OPC variable property data that address-space construction set up.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaCleanupRecursionTests
{
    private readonly TestLogger _logger;

    public OpcUaCleanupRecursionTests(ITestOutputHelper output)
    {
        _logger = new TestLogger(output);
    }

    [Fact]
    public async Task WhenSubjectWithNestedAttributesDetaches_ThenAllAttributeNodeDataIsCleared()
    {
        // Arrange
        using var portLease = await OpcUaTestPortPool.AcquireAsync();
        await using var server = new OpcUaTestServer<CleanupRecursionRoot>(_logger);
        await server.StartAsync(
            createRoot: context =>
            {
                var root = new CleanupRecursionRoot(context);
                root.Child = new CleanupRecursionChild { Number = 42m };
                return root;
            },
            baseAddress: portLease.BaseAddress,
            certificateStoreBasePath: portLease.CertificateStoreBasePath);

        var child = server.Root!.Child!;
        var registered = child.TryGetRegisteredSubject()!;
        var number = registered.TryGetProperty(nameof(CleanupRecursionChild.Number))!;
        var unit = number.TryGetAttribute("Unit")!;
        var source = unit.TryGetAttribute("Source")!;

        await AsyncTestHelpers.WaitUntilAsync(
            () => HasOpcVariable(number) && HasOpcVariable(unit) && HasOpcVariable(source),
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should populate variable data on the property and both nested attribute levels.");

        // Act
        server.Root.Child = null;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => !HasOpcVariable(number) && !HasOpcVariable(unit) && !HasOpcVariable(source),
            timeout: TimeSpan.FromSeconds(30),
            message: "Detach must remove variable data from the property and both nested attribute levels.");
    }

    private static bool HasOpcVariable(RegisteredSubjectProperty property) =>
        property.Reference.TryGetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, out _);
}
