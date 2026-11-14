using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// A request completed a second time can hand its response to a later request. These pin that the server
/// ignores the second completion, so later calls still receive the responses to their own requests.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerRequestCompletionTests
{
    private const string CompleteTwiceValue = "complete-twice";

    private readonly ITestOutputHelper _output;

    public OpcUaServerRequestCompletionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheServerCompletesARequestTwice_ThenLaterCallsReceiveTheirOwnResponses()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var serverRoot = new TestRoot(serverContext)
        {
            Connected = true,
            Name = "initial"
        };

        var configuration = new OpcUaServerConfiguration
        {
            RootName = "Root",
            BaseAddress = port.BaseAddress,
            ValueConverter = new OpcUaValueConverter(),
            CleanCertificateStore = false,
            AutoAcceptUntrustedCertificates = true,
            CertificateStoreBasePath = port.CertificateStoreBasePath
        };

        var subjectServer = new OpcUaSubjectServer(serverRoot, configuration, NullLogger.Instance);
        var application = await configuration.CreateApplicationInstanceAsync();
        await subjectServer.LoadApplicationCertificateAsync(application, CancellationToken.None);

        using var server = new CompletingTwiceServer(serverRoot, subjectServer, configuration, NullLogger.Instance);
        await application.StartAsync(server);

        try
        {
            await using var client = new OpcUaTestClient<TestRoot>(logger);
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            var source = client.Source!;
            Assert.True(source.TryGetNodeId(new PropertyReference(client.Root!, nameof(TestRoot.Name)), out var nameNodeId));
            var session = source.CurrentSession!;

            // Act
            var completedTwice = await session.WriteAsync(
                requestHeader: null,
                [CreateWriteValue(nameNodeId, CompleteTwiceValue)],
                CancellationToken.None);

            Assert.True(StatusCode.IsGood(completedTwice.Results[0]));
            Assert.True(server.CompleteCapturedRequestAgain());

            // Assert
            // Interleaved writes and reads, so a response handed to the wrong request shows up as a type,
            // a request handle or a value that does not match.
            for (var i = 0; i < 50; i++)
            {
                var value = "value-" + i;

                var writeHeader = new RequestHeader();
                var writeResponse = await session.WriteAsync(
                    writeHeader,
                    [CreateWriteValue(nameNodeId, value)],
                    CancellationToken.None);

                Assert.Equal(writeHeader.RequestHandle, writeResponse.ResponseHeader.RequestHandle);
                Assert.True(StatusCode.IsGood(writeResponse.Results[0]));

                var readHeader = new RequestHeader();
                var readResponse = await session.ReadAsync(
                    readHeader,
                    maxAge: 0,
                    TimestampsToReturn.Neither,
                    [new ReadValueId { NodeId = nameNodeId, AttributeId = Opc.Ua.Attributes.Value }],
                    CancellationToken.None);

                Assert.Equal(readHeader.RequestHandle, readResponse.ResponseHeader.RequestHandle);
                Assert.Equal(value, readResponse.Results[0].Value);
            }
        }
        finally
        {
            await application.StopAsync();
        }
    }

    private static WriteValue CreateWriteValue(NodeId nodeId, string value) => new()
    {
        NodeId = nodeId,
        AttributeId = Opc.Ua.Attributes.Value,
        Value = new DataValue(new Variant(value))
    };

    /// <summary>
    /// Captures the write that carries <see cref="CompleteTwiceValue"/> so the test can complete it again
    /// after its response was delivered, the shape of the SDK's own double completion.
    /// </summary>
    private sealed class CompletingTwiceServer : OpcUaStandardServer
    {
        private IEndpointIncomingRequest? _capturedRequest;

        public CompletingTwiceServer(IInterceptorSubject subject, OpcUaSubjectServer source, OpcUaServerConfiguration configuration, ILogger logger)
            : base(subject, source, configuration, logger)
        {
        }

        public bool CompleteCapturedRequestAgain()
        {
            var request = Volatile.Read(ref _capturedRequest);
            request?.OperationCompleted(null, StatusCodes.BadServerHalted);
            return request is not null;
        }

        protected override Task ProcessRequestAsync(IEndpointIncomingRequest request, CancellationToken cancellationToken = default)
        {
            // Captured before the call, so it is in place by the time the client has the response.
            if (request.Request is WriteRequest writeRequest &&
                writeRequest.NodesToWrite.Exists(writeValue => Equals(writeValue.Value?.Value, CompleteTwiceValue)))
            {
                Interlocked.CompareExchange(ref _capturedRequest, request, null);
            }

            return base.ProcessRequestAsync(request, cancellationToken);
        }
    }
}
