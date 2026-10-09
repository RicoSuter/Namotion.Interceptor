using System.Collections.Specialized;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Events;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosEventListenerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

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
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");

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
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");
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
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");
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
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");
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
        listener.Start("127.0.0.1", LoopbackHttpServer.GetFreePort(), listenHost: "127.0.0.1");
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        await listener.UnsubscribeAllAsync(CancellationToken.None);

        // Assert
        Assert.Equal("uuid:sub-1", await unsubscribeSid.Task.WaitAsync(WaitTimeout));
        Assert.Empty(listener.Subscriptions);
        Assert.Equal("RINCON_X/AVTransport", subscription.Key);
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
        listener.Start("127.0.0.1", LoopbackHttpServer.GetFreePort(), listenHost: "127.0.0.1");
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        var renewed = await listener.RenewAsync(subscription, CancellationToken.None);

        // Assert
        Assert.False(renewed);
        Assert.Empty(listener.Subscriptions);
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
