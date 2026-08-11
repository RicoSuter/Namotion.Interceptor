using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>Int32-backed and non-nullable, the only shape the inbound coercion can restore from a boxed int.</summary>
public enum WriteIntegrityMode
{
    Idle = 0,
    Running = 1,
    Faulted = 2
}

[InterceptorSubject]
public partial class WriteIntegrityRoot
{
    [Path("opc", "Child")]
    public partial WriteIntegrityChild? Child { get; set; }
}

[InterceptorSubject]
public partial class WriteIntegrityChild
{
    /// <summary>The value a validation interceptor is pointed at, so a client write can be refused.</summary>
    public const string RejectedValue = "rejected";

    /// <summary>The value the generated hook vetoes, so a write can be refused without an exception.</summary>
    public const string VetoedValue = "vetoed";

    public WriteIntegrityChild()
    {
        Numbers = [1, 2, 3, 4, 5];
    }

    /// <summary>A plain writable property: the baseline for an accepted write and the target of the validation tests.</summary>
    [Path("opc", "Value")]
    public partial string? Value { get; set; }

    /// <summary>A sibling, so a test can prove one node's failure does not take the rest of the request down.</summary>
    [Path("opc", "Other")]
    public partial string? Other { get; set; }

    /// <summary>An array, so a client can write an index range into it.</summary>
    [Path("opc", "Numbers")]
    public partial int[] Numbers { get; set; }

    /// <summary>An enum, which reaches the property setter as a boxed int unless something coerces it back.</summary>
    [Path("opc", "Mode")]
    public partial WriteIntegrityMode Mode { get; set; }

    /// <summary>Clamped inbound by <see cref="ClampingValueConverter"/>, so the converter pair does not round-trip.</summary>
    [Path("opc", "ClampedValue")]
    public partial double ClampedValue { get; set; }

    /// <summary>Refused by the generated hook for one value, which cancels the write without throwing.</summary>
    [Path("opc", "Vetoed")]
    public partial string? Vetoed { get; set; }

    partial void OnVetoedChanging(ref string? newValue, ref bool cancel)
    {
        if (newValue == VetoedValue)
        {
            cancel = true;
        }
    }
}

/// <summary>
/// Clamps one property's inbound value, so what the model accepts is provably not what the client sent
/// while every other property still round-trips. Stands in for the scaling, unit and enum mapping
/// converters a real deployment carries.
/// </summary>
internal sealed class ClampingValueConverter : OpcUaValueConverter
{
    public const double Maximum = 100d;

    public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
    {
        var converted = base.ConvertToPropertyValue(nodeValue, property);
        if (property.Name == nameof(WriteIntegrityChild.ClampedValue) && converted is double value && value > Maximum)
        {
            return Maximum;
        }

        return converted;
    }
}

/// <summary>
/// A server over <see cref="WriteIntegrityRoot"/> plus a raw session, which is everything a test needs to
/// issue a client write and then compare what the node serves against what the subject holds. Dedicated
/// rather than built on the shared assembly fixture: several of these tests install a converter or a write
/// interceptor that changes the whole context's behaviour, which on a shared server would leak into every
/// other test in the assembly.
/// </summary>
internal sealed class WriteIntegrityFixture : IAsyncDisposable
{
    public const string InitialValue = "initial";

    private readonly PortLease _port;
    private readonly OpcUaTestServer<WriteIntegrityRoot> _server;

    private WriteIntegrityFixture(
        PortLease port,
        OpcUaTestServer<WriteIntegrityRoot> server,
        RawOpcUaTestSession session)
    {
        _port = port;
        _server = server;
        Session = session;
    }

    public RawOpcUaTestSession Session { get; }

    public WriteIntegrityRoot Root => _server.Root!;

    public WriteIntegrityChild Child => Root.Child!;

    public IOpcUaSubjectServer Server => _server.Server!;

    public IInterceptorSubjectContext Context => _server.Context;

    public PropertyReference Property(string name) => new(Child, name);

    public static async Task<WriteIntegrityFixture> StartAsync(
        ITestOutputHelper output,
        OpcUaValueConverter? valueConverter = null,
        IWriteInterceptor? writeInterceptor = null)
    {
        var logger = new TestLogger(output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<WriteIntegrityRoot>(logger);
        RawOpcUaTestSession? session = null;
        try
        {
            await server.StartAsync(
                createRoot: context =>
                {
                    if (writeInterceptor is not null)
                    {
                        context.AddService(writeInterceptor);
                    }

                    return new WriteIntegrityRoot(context);
                },
                initializeDefaults: (context, root) => root.Child = new WriteIntegrityChild(context)
                {
                    Value = InitialValue,
                    Other = InitialValue,
                    Vetoed = InitialValue
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath,
                valueConverter: valueConverter);

            // The node tree is built during CreateAddressSpace, so a test can address it immediately once
            // any one node exists.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Server!.TryGetVariableNode(
                    new PropertyReference(server.Root!.Child!, nameof(WriteIntegrityChild.Value)), out _),
                message: "the child's variable node should exist");

            session = await RawOpcUaTestSession.ConnectAsync(port.ServerUrl, port.CertificateStoreBasePath);
            return new WriteIntegrityFixture(port, server, session);
        }
        catch
        {
            // A leaked lease would starve the port pool's parallel slots for the rest of the run.
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            await server.DisposeAsync();
            port.Dispose();
            throw;
        }
    }

    /// <summary>Resolves the node backing a property of the child, which is how a test addresses it on the wire.</summary>
    public BaseDataVariableState Node(string propertyName)
    {
        if (!Server.TryGetVariableNode(Property(propertyName), out var node))
        {
            throw new InvalidOperationException($"No variable node exists for '{propertyName}'.");
        }

        return node;
    }

    public NodeId NodeId(string propertyName) => Node(propertyName).NodeId;

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        await _server.DisposeAsync();
        _port.Dispose();
    }
}
