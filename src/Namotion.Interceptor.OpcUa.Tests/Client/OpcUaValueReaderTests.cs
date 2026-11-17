using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaValueReaderTests
{
    [Theory]
    [InlineData(null, 5, 5)]
    [InlineData(0u, 5, 5)]
    [InlineData(2u, 5, 2)]
    [InlineData(5u, 5, 5)]
    [InlineData(10u, 5, 5)]
    public void WhenTheServerReportsAReadLimit_ThenNodesPerReadIsTheMaximumCappedAtIt(uint? maxNodesPerRead, int maximum, int expected)
    {
        // Arrange
        var session = new FakeReadServer(maxNodesPerRead).Session;

        // Act
        var nodesPerRead = OpcUaValueReader.GetNodesPerRead(session, maximum);

        // Assert
        Assert.Equal(expected, nodesPerRead);
    }

    [Fact]
    public async Task WhenTheResponseAnswersTheRequest_ThenTheResultsAreReturnedInNodeOrder()
    {
        // Arrange
        var server = new FakeReadServer(maxNodesPerRead: null);

        // Act
        var results = await OpcUaValueReader.ReadAsync(
            server.Session, CancellationToken.None, CreateNodesToRead(3), TimestampsToReturn.Source, CancellationToken.None);

        // Assert
        Assert.Equal(["Node0", "Node1", "Node2"], results.Select(result => (string)result.Value));
    }

    [Fact]
    public async Task WhenSeveralReadsAreSent_ThenEachGetsItsOwnHeader()
    {
        // Arrange
        var server = new FakeReadServer(maxNodesPerRead: null);

        // Act
        for (var i = 0; i < 3; i++)
        {
            await OpcUaValueReader.ReadAsync(
                server.Session, CancellationToken.None, CreateNodesToRead(1), TimestampsToReturn.Source, CancellationToken.None);
        }

        // Assert
        Assert.Equal(3, server.RequestHeaders.Distinct().Count());
    }

    [Fact]
    public async Task WhenTheResponseAnswersADifferentRequest_ThenTheReadFailsAsAMismatch()
    {
        // Arrange
        var server = new FakeReadServer(maxNodesPerRead: null) { ResponseHandleOffset = 1 };

        // Act & Assert
        await Assert.ThrowsAsync<OpcUaResponseMismatchException>(async () => await OpcUaValueReader.ReadAsync(
            server.Session, CancellationToken.None, CreateNodesToRead(1), TimestampsToReturn.Source, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheReadReceivesAResponseOfAnotherServiceType_ThenItFailsAsAMismatch()
    {
        // Arrange
        var session = new Mock<ISession>();
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidCastException("Unable to cast object of type 'Opc.Ua.PublishResponse' to type 'Opc.Ua.ReadResponse'."));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaResponseMismatchException>(async () => await OpcUaValueReader.ReadAsync(
            session.Object, CancellationToken.None, CreateNodesToRead(1), TimestampsToReturn.Source, CancellationToken.None));
        Assert.Contains("PublishResponse", exception.Message);
    }

    [Theory]
    [InlineData(-1, new[] { "Node0" })]
    [InlineData(2, new[] { "Node0", "Node1" })]
    public async Task WhenTheResponseCarriesFewerOrMoreResultsThanNodes_ThenOnlyResultsForRequestedNodesAreReturned(
        int resultCountAdjustment, string[] expected)
    {
        // Arrange
        var server = new FakeReadServer(maxNodesPerRead: null) { ResultCountAdjustment = resultCountAdjustment };

        // Act
        var results = await OpcUaValueReader.ReadAsync(
            server.Session, CancellationToken.None, CreateNodesToRead(2), TimestampsToReturn.Source, CancellationToken.None);

        // Assert
        Assert.Equal(expected, results.Select(result => (string)result.Value));
    }

    private static ReadValueIdCollection CreateNodesToRead(int count) =>
        new(Enumerable.Range(0, count).Select(index => new ReadValueId
        {
            NodeId = new NodeId("Node" + index, 2),
            AttributeId = Opc.Ua.Attributes.Value
        }));

    /// <summary>
    /// Assigns request handles the way the SDK does while sending, and answers each node with its identifier.
    /// </summary>
    private sealed class FakeReadServer
    {
        private uint _nextHandle;

        public FakeReadServer(uint? maxNodesPerRead)
        {
            var session = new Mock<ISession>();
            session.SetupGet(value => value.OperationLimits)
                .Returns(maxNodesPerRead is null ? null! : new OperationLimits { MaxNodesPerRead = maxNodesPerRead.Value });
            session
                .Setup(value => value.ReadAsync(
                    It.IsAny<RequestHeader>(),
                    It.IsAny<double>(),
                    It.IsAny<TimestampsToReturn>(),
                    It.IsAny<ReadValueIdCollection>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((RequestHeader requestHeader, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
                    Answer(requestHeader, nodesToRead));
            Session = session.Object;
        }

        public ISession Session { get; }

        public List<RequestHeader> RequestHeaders { get; } = [];

        /// <summary>Gets or sets what the response handle adds to the request handle.</summary>
        public uint ResponseHandleOffset { get; init; }

        /// <summary>Gets or sets how many results a response carries beyond its node count.</summary>
        public int ResultCountAdjustment { get; init; }

        private ReadResponse Answer(RequestHeader requestHeader, ReadValueIdCollection nodesToRead)
        {
            RequestHeaders.Add(requestHeader);
            requestHeader.RequestHandle = ++_nextHandle;

            var identifiers = nodesToRead.Select(node => (string)node.NodeId.Identifier).ToArray();
            return new ReadResponse
            {
                ResponseHeader = new ResponseHeader { RequestHandle = requestHeader.RequestHandle + ResponseHandleOffset },
                Results = new DataValueCollection(Enumerable.Range(0, identifiers.Length + ResultCountAdjustment).Select(index => new DataValue
                {
                    Value = index < identifiers.Length ? identifiers[index] : "Extra",
                    StatusCode = StatusCodes.Good
                })),
                DiagnosticInfos = []
            };
        }
    }
}
