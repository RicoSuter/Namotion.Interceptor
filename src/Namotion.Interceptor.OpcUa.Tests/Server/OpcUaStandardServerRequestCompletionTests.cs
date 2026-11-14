using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// The SDK completes a request a second time in some shutdown races, and a second completion can deliver
/// a response to the wrong request. These pin that every request the server schedules completes once.
/// </summary>
public class OpcUaStandardServerRequestCompletionTests
{
    [Fact]
    public async Task WhenCallAsyncAndOperationCompletedRace_ThenTheRequestIsCompletedOnce()
    {
        // Arrange
        const int iterations = 2000;
        var requests = new CountingRequest[iterations];
        for (var i = 0; i < iterations; i++)
        {
            requests[i] = new CountingRequest();
        }

        var guardedRequests = requests.Select(request => new SingleCompletionIncomingRequest(request)).ToArray();
        using var barrier = new Barrier(2);

        // Act
        var caller = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            foreach (var guardedRequest in guardedRequests)
            {
                Assert.True(barrier.SignalAndWait(TestTimeouts.Default), "The other race participant never reached the barrier.");
                guardedRequest.CallAsync().AsTask().GetAwaiter().GetResult();
            }
        });

        var completer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            foreach (var guardedRequest in guardedRequests)
            {
                Assert.True(barrier.SignalAndWait(TestTimeouts.Default), "The other race participant never reached the barrier.");
                guardedRequest.OperationCompleted(null, StatusCodes.BadServerHalted);
            }
        });

        await Task.WhenAll(caller, completer);

        // Assert
        Assert.All(requests, request => Assert.Equal(1, request.CompletionCount));
    }

    [Fact]
    public async Task WhenOperationCompletedClaimsTheRequestFirst_ThenCallAsyncDoesNotRunTheService()
    {
        // Arrange
        var request = new CountingRequest();
        var guardedRequest = new SingleCompletionIncomingRequest(request);

        // Act
        guardedRequest.OperationCompleted(null, StatusCodes.BadServerHalted);
        await guardedRequest.CallAsync();
        guardedRequest.OperationCompleted(null, StatusCodes.BadInternalError);

        // Assert
        Assert.Equal(1, request.CompletionCount);
        Assert.Equal(0, request.CallCount);
        Assert.Equal(StatusCodes.BadServerHalted, request.Error?.StatusCode.Code);
    }

    [Fact]
    public async Task WhenCallAsyncClaimsTheRequestFirst_ThenALaterOperationCompletedIsIgnored()
    {
        // Arrange
        var request = new CountingRequest();
        var guardedRequest = new SingleCompletionIncomingRequest(request);

        // Act
        await guardedRequest.CallAsync();
        guardedRequest.OperationCompleted(null, StatusCodes.BadServerHalted);

        // Assert
        Assert.Equal(1, request.CompletionCount);
        Assert.Equal(1, request.CallCount);
        Assert.Null(request.Error);
    }

    [Fact]
    public void WhenSchedulingThrowsAfterTheQueueCompletedTheRequest_ThenItIsNotCompletedAgainAndNothingIsThrown()
    {
        // Arrange
        // A disposed queue completes the request with BadServerHalted while scheduling, and the request
        // throws from that completion, so the scheduling call throws after a completion was made: the shape
        // of the SDK's enqueue that throws once the queue is disposed.
        using var server = CreateServer();
        server.Dispose();
        var request = new CountingRequest { ThrowOnOperationCompleted = true };

        // Act
        server.ScheduleIncomingRequest(request);

        // Assert
        Assert.Equal(1, request.CompletionCount);
        Assert.Equal(StatusCodes.BadServerHalted, request.Error?.StatusCode.Code);
    }

    [Fact]
    public async Task WhenTheServerIsDisposedWhileRequestsAreScheduled_ThenEachRequestIsCompletedOnce()
    {
        // Arrange
        // Disposing the request queue completes the requests still queued while a worker can dequeue the
        // same request and process it, which without the guard completes some of them twice.
        const int iterations = 50;
        const int producerCount = 4;
        var requests = new ConcurrentQueue<CountingRequest>();

        // Act
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var server = CreateServer();
            using var stop = new CancellationTokenSource();
            var scheduledBefore = requests.Count;

            var producers = Enumerable.Range(0, producerCount)
                .Select(_ => DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var request = new CountingRequest();
                        requests.Enqueue(request);
                        server.ScheduleIncomingRequest(request);
                    }
                }))
                .ToArray();

            await AsyncTestHelpers.WaitUntilAsync(
                () => requests.Count - scheduledBefore >= 200,
                pollInterval: TimeSpan.FromMilliseconds(1),
                message: "The producers should schedule requests before the server is disposed.");

            server.Dispose();
            await stop.CancelAsync();
            await Task.WhenAll(producers);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => requests.All(request => request.CompletionCount > 0),
            message: () => $"{requests.Count(request => request.CompletionCount == 0)} requests were never completed.");

        // Assert
        Assert.Equal(0, requests.Count(request => request.CompletionCount > 1));
    }

    private static OpcUaStandardServer CreateServer()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var subject = new DeliveryRuleTestRoot(context);
        var configuration = new OpcUaServerConfiguration();
        var subjectServer = new OpcUaSubjectServer(subject, configuration, NullLogger.Instance);
        return new OpcUaStandardServer(subject, subjectServer, configuration, NullLogger.Instance);
    }

    /// <summary>
    /// Counts completions the way the SDK's request turns them into responses: the service call completes
    /// the request when it returns, and <see cref="OperationCompleted"/> completes it directly.
    /// </summary>
    private sealed class CountingRequest : IEndpointIncomingRequest
    {
        private int _completionCount;
        private int _callCount;

        public bool ThrowOnOperationCompleted { get; init; }

        public int CompletionCount => Volatile.Read(ref _completionCount);

        public int CallCount => Volatile.Read(ref _callCount);

        public ServiceResult? Error { get; private set; }

        public IServiceRequest Request { get; } = new ReadRequest { RequestHeader = new RequestHeader() };

        public SecureChannelContext SecureChannelContext => null!;

        public async ValueTask CallAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            Interlocked.Increment(ref _completionCount);
        }

        public void OperationCompleted(IServiceResponse response, ServiceResult error)
        {
            Error = error;
            Interlocked.Increment(ref _completionCount);
            if (ThrowOnOperationCompleted)
            {
                throw new ObjectDisposedException("RequestQueue");
            }
        }
    }
}
