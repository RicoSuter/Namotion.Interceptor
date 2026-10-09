using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Events;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosEventListenerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NegativeCheckDuration = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task WhenSubscribing_ThenSendsCallbackNotificationTypeAndTimeout()
    {
        // Arrange
        var headers = new TaskCompletionSource<NameValueCollection>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(context =>
        {
            headers.TrySetResult(context.Request.Headers);
            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));

        // Act
        var subscription = await listener.SubscribeAsync(
            "RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/MediaRenderer/AVTransport/Event"), _ => { }, CancellationToken.None);

        // Assert
        var received = await headers.Task.WaitAsync(WaitTimeout);
        Assert.Equal($"<http://127.0.0.1:{port}/event/RINCON_X/AVTransport>", received["CALLBACK"]);
        Assert.Equal("upnp:event", received["NT"]);
        Assert.Equal("Second-1800", received["TIMEOUT"]);
        Assert.Equal("uuid:sub-1", subscription.Sid);
        Assert.InRange(subscription.RenewAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task WhenNotifyCarriesKnownSid_ThenHandlerReceivesBody()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), body => received.TrySetResult(body), CancellationToken.None);

        // Act
        var status = await SendNotifyAsync(httpClient, $"http://127.0.0.1:{port}/event/RINCON_X/AVTransport", "uuid:sub-1", "<body />");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("<body />", await received.Task.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenNotifyCarriesUnknownSid_ThenReturnsPreconditionFailed()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var handlerCalls = 0;
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => handlerCalls++, CancellationToken.None);

        // Act
        var status = await SendNotifyAsync(httpClient, $"http://127.0.0.1:{port}/event/RINCON_X/AVTransport", "uuid:stale", "<body />");

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task WhenNotifyArrivesBeforeSubscribeResponse_ThenHandlerStillReceivesIt()
    {
        // Arrange
        using var httpClient = new HttpClient();
        var notifyStatus = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            // Sonos sends the initial full state before answering SUBSCRIBE.
            var callback = context.Request.Headers["CALLBACK"]!.Trim('<', '>');
            notifyStatus.TrySetResult(await SendNotifyAsync(httpClient, callback, "uuid:sub-1", "<initial />"));
            await RespondWithSid(context, "uuid:sub-1");
        });
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), body => received.TrySetResult(body), CancellationToken.None);

        // Assert
        Assert.Equal(HttpStatusCode.OK, await notifyStatus.Task.WaitAsync(WaitTimeout));
        Assert.Equal("<initial />", await received.Task.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenUnsubscribing_ThenSendsUnsubscribeWithSidAndForgetsTheSubscription()
    {
        // Arrange
        var unsubscribeSid = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(context =>
        {
            if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                unsubscribeSid.TrySetResult(context.Request.Headers["SID"]);
                return Task.CompletedTask;
            }

            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        await listener.UnsubscribeAllAsync(CancellationToken.None);

        // Assert
        Assert.Equal("uuid:sub-1", await unsubscribeSid.Task.WaitAsync(WaitTimeout));
        Assert.Empty(listener.Subscriptions);
        Assert.Equal("RINCON_X/AVTransport", subscription.Key);
    }

    [Fact]
    public async Task WhenUnsubscribingAll_ThenRequestsAreSentConcurrently()
    {
        // Arrange
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivedWhileOtherWasPending = new System.Collections.Concurrent.ConcurrentBag<bool>();
        var arrivals = 0;
        var nextSid = 0;
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                // Each UNSUBSCRIBE is held until both arrived, which a sequential teardown never achieves.
                if (Interlocked.Increment(ref arrivals) == 2)
                {
                    bothArrived.TrySetResult();
                }

                try
                {
                    await bothArrived.Task.WaitAsync(WaitTimeout / 2);
                    arrivedWhileOtherWasPending.Add(true);
                }
                catch (TimeoutException)
                {
                    arrivedWhileOtherWasPending.Add(false);
                }

                return;
            }

            await RespondWithSid(context, $"uuid:sub-{Interlocked.Increment(ref nextSid)}");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/AVTransport/Event"), _ => { }, CancellationToken.None);
        await listener.SubscribeAsync("RINCON_X/RenderingControl", new Uri(speaker.BaseUri, "/RenderingControl/Event"), _ => { }, CancellationToken.None);

        // Act
        await listener.UnsubscribeAllAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new[] { true, true }, arrivedWhileOtherWasPending);
        Assert.Empty(listener.Subscriptions);
    }

    [Fact]
    public async Task WhenRenewalIsRejected_ThenSubscriptionIsForgotten()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context =>
        {
            if (context.Request.Headers["SID"] is not null)
            {
                context.Response.StatusCode = 412;
                return Task.CompletedTask;
            }

            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        var renewed = await listener.RenewAsync(subscription, CancellationToken.None);

        // Assert
        Assert.False(renewed);
        Assert.Empty(listener.Subscriptions);
    }

    [Fact]
    public async Task WhenRequestIsNotNotify_ThenReturnsMethodNotAllowed()
    {
        // Arrange
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));

        // Act
        using var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/event/RINCON_X/AVTransport");

        // Assert
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task WhenHandlerThrows_ThenNotifyStillReturnsOk()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        await listener.SubscribeAsync(
            "RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        // Act
        var status = await SendNotifyAsync(httpClient, $"http://127.0.0.1:{port}/event/RINCON_X/AVTransport", "uuid:sub-1", "<body />");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task WhenNotifyDeclaresOversizedBody_ThenReturnsRequestEntityTooLarge()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var handlerCalls = 0;
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => handlerCalls++, CancellationToken.None);

        // Act
        // A raw socket declares the length without sending the body, so the listener must answer without reading it.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var requestBytes = Encoding.ASCII.GetBytes(
            $"NOTIFY /event/RINCON_X/AVTransport HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nSID: uuid:sub-1\r\nNT: upnp:event\r\nNTS: upnp:propchange\r\nContent-Length: {2 * 1024 * 1024}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(requestBytes);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync().WaitAsync(WaitTimeout);

        // Assert
        Assert.Equal("HTTP/1.1 413 Request Entity Too Large", statusLine);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task WhenSpeakerGrantsATinyLifetime_ThenRenewalWaitsForHalfTheMinimumLifetime()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context =>
        {
            context.Response.Headers["SID"] = "uuid:sub-1";
            context.Response.Headers["TIMEOUT"] = "Second-0";
            return Task.CompletedTask;
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));

        // Act
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Assert
        Assert.InRange(subscription.RenewAt, DateTimeOffset.UtcNow.AddSeconds(25), DateTimeOffset.UtcNow.AddSeconds(31));
    }

    [Fact]
    public async Task WhenSubscribingTheSameKeyTwice_ThenThrowsAndKeepsTheFirstSubscription()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var first = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None));
        Assert.Same(first, Assert.Single(listener.Subscriptions));
    }

    [Fact]
    public async Task WhenSubscribingBeforeStart_ThenThrows()
    {
        // Arrange
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listener.SubscribeAsync("RINCON_X/AVTransport", new Uri("http://127.0.0.1:1/Event"), _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task WhenStartedTwice_ThenThrows()
    {
        // Arrange
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            listener.Start("127.0.0.1", LoopbackHttpServer.GetFreePort(), listenHost: "127.0.0.1"));
    }

    [Fact]
    public async Task WhenRenewalSucceeds_ThenRenewAtMovesToHalfTheGrantedLifetime()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context =>
        {
            if (context.Request.Headers["SID"] is not null)
            {
                context.Response.Headers["SID"] = "uuid:sub-1";
                context.Response.Headers["TIMEOUT"] = "Second-600";
                context.Response.StatusCode = 200;
                return Task.CompletedTask;
            }

            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        var renewed = await listener.RenewAsync(subscription, CancellationToken.None);

        // Assert
        Assert.True(renewed);
        Assert.InRange(subscription.RenewAt, DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.Same(subscription, Assert.Single(listener.Subscriptions));
    }

    [Fact]
    public async Task WhenRenewingSubscriptionWithoutSid_ThenThrows()
    {
        // Arrange
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var subscription = new SonosEventSubscription("RINCON_X/AVTransport", new Uri("http://127.0.0.1:1/Event"), _ => { });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.RenewAsync(subscription, CancellationToken.None));
    }

    [Fact]
    public async Task WhenUnsubscribedWhileSubscribeIsPending_ThenSubscribeThrowsAndTheSpeakerIsToldToDropIt()
    {
        // Arrange
        var subscribeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribeSid = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                unsubscribeSid.TrySetResult(context.Request.Headers["SID"]);
                return;
            }

            subscribeReceived.TrySetResult();
            await releaseSubscribe.Task;
            await RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var pending = listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);
        await subscribeReceived.Task.WaitAsync(WaitTimeout);

        // Act
        await listener.UnsubscribeAsync(Assert.Single(listener.Subscriptions), CancellationToken.None);
        releaseSubscribe.SetResult();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal("uuid:sub-1", await unsubscribeSid.Task.WaitAsync(WaitTimeout));
        Assert.Empty(listener.Subscriptions);
    }

    [Theory]
    [InlineData(500, LogLevel.Information)]
    [InlineData(412, LogLevel.Debug)]
    public async Task WhenUnsubscribeIsAnsweredWithAnError_ThenOnlyAnUnknownSubscriptionIsLoggedAtDebug(int statusCode, LogLevel expectedLevel)
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context =>
        {
            if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            }

            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        var logger = new RecordingLogger();
        await using var listener = new SonosEventListener(httpClient, logger);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        await listener.UnsubscribeAllAsync(CancellationToken.None);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Contains("RINCON_X/AVTransport", entry.Message);
    }

    [Fact]
    public async Task WhenTheOrphanOfAPendingSubscribeCannotBeUnsubscribed_ThenItIsLoggedAtInformation()
    {
        // Arrange
        var subscribeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                context.Response.StatusCode = 500;
                return;
            }

            subscribeReceived.TrySetResult();
            await releaseSubscribe.Task;
            await RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        var logger = new RecordingLogger();
        await using var listener = new SonosEventListener(httpClient, logger);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var pending = listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);
        await subscribeReceived.Task.WaitAsync(WaitTimeout);

        // Act
        await listener.UnsubscribeAllAsync(CancellationToken.None);
        releaseSubscribe.SetResult();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("uuid:sub-1", entry.Message);
    }

    [Fact]
    public async Task WhenNotifyDeclaresNoLengthAndBodyIsOversized_ThenReturnsRequestEntityTooLarge()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var handlerCalls = 0;
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => handlerCalls++, CancellationToken.None);

        // Act
        // The oversized chunk is the last write and has no terminator, so the server never closes over unread data.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        var headerBytes = Encoding.ASCII.GetBytes(
            $"NOTIFY /event/RINCON_X/AVTransport HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nSID: uuid:sub-1\r\nNT: upnp:event\r\nNTS: upnp:propchange\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
        // The declared chunk is larger than what is sent, so the cap is hit while the chunk is still incomplete.
        var declaredChunkSize = 2 * 1024 * 1024;
        var sentBytes = new byte[1024 * 1024 + 16 * 1024];
        Array.Fill(sentBytes, (byte)'a');
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{declaredChunkSize:X}\r\n"));
        await stream.WriteAsync(sentBytes);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync().WaitAsync(WaitTimeout);

        // Assert
        Assert.Equal("HTTP/1.1 413 Request Entity Too Large", statusLine);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task WhenStartedAndDisposed_ThenIsListeningFollowsTheLifecycle()
    {
        // Arrange
        using var httpClient = new HttpClient();
        var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var wasListeningBeforeStart = listener.IsListening;

        // Act
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var wasListeningAfterStart = listener.IsListening;
        await listener.DisposeAsync();

        // Assert
        Assert.False(wasListeningBeforeStart);
        Assert.True(wasListeningAfterStart);
        Assert.False(listener.IsListening);
    }

    [Fact]
    public async Task WhenUsedAfterDispose_ThenStartAndSubscribeThrowObjectDisposed()
    {
        // Arrange
        using var httpClient = new HttpClient();
        var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        await listener.DisposeAsync();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() =>
            listener.Start("127.0.0.1", LoopbackHttpServer.GetFreePort(), listenHost: "127.0.0.1"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            listener.SubscribeAsync("RINCON_X/AVTransport", new Uri("http://127.0.0.1:1/Event"), _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task WhenPortIsTaken_ThenStartThrowsAndTheListenerCanStartOnAnotherPort()
    {
        // Arrange
        using var httpClient = new HttpClient();
        await using var first = new SonosEventListener(httpClient, NullLogger.Instance);
        await using var second = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => first.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));

        // Act & Assert
        Assert.Throws<HttpListenerException>(() => second.Start("127.0.0.1", port, listenHost: "127.0.0.1"));
        LoopbackHttpServer.StartOnFreePort(candidate => second.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        Assert.True(second.IsListening);
    }

    [Fact]
    public async Task WhenHandlerIsRunning_ThenDisposeWaitsForItToFinish()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.StartOnFreePort(candidate => listener.Start("127.0.0.1", candidate, listenHost: "127.0.0.1"));
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = false;
        await listener.SubscribeAsync(
            "RINCON_X/AVTransport",
            new Uri(speaker.BaseUri, "/Event"),
            _ =>
            {
                handlerStarted.SetResult();
                releaseHandler.Task.Wait(WaitTimeout);
                handlerFinished = true;
            },
            CancellationToken.None);
        var notifyTask = SendNotifyAsync(httpClient, $"http://127.0.0.1:{port}/event/RINCON_X/AVTransport", "uuid:sub-1", "<body />");
        await handlerStarted.Task.WaitAsync(WaitTimeout);

        // Act
        var disposeTask = listener.DisposeAsync().AsTask();
        // A negative check has to observe for some time; a correct implementation never fails it.
        var completedWhileHandlerRuns = true;
        try
        {
            await disposeTask.WaitAsync(NegativeCheckDuration);
        }
        catch (TimeoutException)
        {
            completedWhileHandlerRuns = false;
        }

        releaseHandler.SetResult();
        await disposeTask.WaitAsync(WaitTimeout);

        // Assert
        Assert.False(completedWhileHandlerRuns);
        Assert.True(handlerFinished);
        try
        {
            await notifyTask.WaitAsync(WaitTimeout);
        }
        catch (HttpRequestException)
        {
            // Disposing closes the connection, so the speaker side may see it reset.
        }
    }

    private static Task RespondWithSid(HttpListenerContext context, string sid)
    {
        context.Response.Headers["SID"] = sid;
        context.Response.Headers["TIMEOUT"] = "Second-1800";
        context.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    private static async Task<HttpStatusCode> SendNotifyAsync(HttpClient httpClient, string callback, string sid, string body)
    {
        using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
        {
            Content = new StringContent(body)
        };
        request.Headers.TryAddWithoutValidation("SID", sid);
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
        using var response = await httpClient.SendAsync(request);
        return response.StatusCode;
    }
}
