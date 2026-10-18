using System.Net;
using Namotion.Devices.Pushover.Client;
using Namotion.Devices.Pushover.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverClientTests
{
    private static Dictionary<string, string> CreateFields() => new() { ["token"] = "app", ["user"] = "user", ["message"] = "Hello" };

    [Fact]
    public async Task WhenSendingMessage_ThenPostsFormEncodedToTheMessagesEndpoint()
    {
        // Arrange
        var handler = new FakePushoverHandler();
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        await client.SendMessageAsync(CreateFields(), CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.pushover.net/1/messages.json", request.Uri.ToString());
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("app", request.Form["token"]);
        Assert.Equal("user", request.Form["user"]);
        Assert.Equal("Hello", request.Form["message"]);
        Assert.False(request.Form.ContainsKey("title"));
        Assert.False(request.Form.ContainsKey("priority"));
    }

    [Theory]
    [InlineData("""{"user":"invalid","errors":["user identifier is invalid"],"status":0,"request":"x"}""", "user identifier is invalid", false)]
    [InlineData("""{"token":"invalid","errors":["application token is invalid"],"status":0}""", "application token is invalid", true)]
    public async Task WhenPushoverRejectsTheUserKeyOrTheApplicationToken_ThenTheExceptionCarriesItsErrors(string body, string expectedMessage, bool expectedApplicationTokenInvalid)
    {
        // Arrange
        var handler = new FakePushoverHandler { Respond = _ => (HttpStatusCode.BadRequest, body) };
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        var exception = await Assert.ThrowsAsync<PushoverException>(() => client.ValidateUserAsync("app", "user", CancellationToken.None));

        // Assert
        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(expectedApplicationTokenInvalid, exception.IsApplicationTokenInvalid);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task WhenResponseIsNotJson_ThenExceptionNamesTheStatusCode()
    {
        // Arrange
        var handler = new FakePushoverHandler
        {
            ResponseContentType = "text/plain",
            Respond = _ => (HttpStatusCode.TooManyRequests, "Too many requests")
        };
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        var exception = await Assert.ThrowsAsync<PushoverException>(() => client.SendMessageAsync(CreateFields(), CancellationToken.None));

        // Assert
        Assert.Equal("Pushover returned HTTP 429.", exception.Message);
    }

    [Fact]
    public async Task WhenResponseIsSuccessfulButStatusIsNotOne_ThenExceptionNamesTheStatusCode()
    {
        // Arrange
        var handler = new FakePushoverHandler { Respond = _ => (HttpStatusCode.OK, """{"status":0,"request":"x"}""") };
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        var exception = await Assert.ThrowsAsync<PushoverException>(() => client.SendMessageAsync(CreateFields(), CancellationToken.None));

        // Assert
        Assert.Equal("Pushover rejected the request (HTTP 200).", exception.Message);
    }

    [Fact]
    public async Task WhenValidatingUser_ThenReturnsItsDevices()
    {
        // Arrange
        var handler = new FakePushoverHandler { Respond = _ => (HttpStatusCode.OK, """{"status":1,"devices":["phone","desktop"]}""") };
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        var response = await client.ValidateUserAsync("app", "user", CancellationToken.None);

        // Assert
        Assert.Equal(["phone", "desktop"], response.Devices!);
        Assert.Equal("/1/users/validate.json", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task WhenGettingReceipt_ThenTokenIsPassedAsQuery()
    {
        // Arrange
        var handler = new FakePushoverHandler
        {
            Respond = _ => (HttpStatusCode.OK, """{"status":1,"acknowledged":1,"acknowledged_at":1700000000,"expired":0}""")
        };
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        var response = await client.GetReceiptAsync("app", "receipt1", CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.pushover.net/1/receipts/receipt1.json?token=app", request.Uri.ToString());
        Assert.Equal(1, response.Acknowledged);
        Assert.Equal(1700000000, response.AcknowledgedAt);
    }

    [Fact]
    public async Task WhenCancelingReceipt_ThenPostsToTheCancelEndpoint()
    {
        // Arrange
        var handler = new FakePushoverHandler();
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        await client.CancelReceiptAsync("app", "receipt1", CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/1/receipts/receipt1/cancel.json", request.Path);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("app", request.Form["token"]);
    }

    [Theory]
    [InlineData("target-rico", "https://api.pushover.net/1/receipts/cancel_by_tag/target-rico.json")]
    [InlineData("a b/c", "https://api.pushover.net/1/receipts/cancel_by_tag/a%20b%2Fc.json")]
    public async Task WhenCancelingByTag_ThenPostsTheTokenToTheEscapedTagPath(string tag, string expectedUri)
    {
        // Arrange
        var handler = new FakePushoverHandler();
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        await client.CancelByTagAsync("app", tag, CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expectedUri, request.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("app", Assert.Single(request.Form).Value);
        Assert.Equal("token", Assert.Single(request.Form).Key);
    }

    [Fact]
    public async Task WhenManyRequestsRunAtOnce_ThenAtMostTwoAreOpen()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakePushoverHandler { BeforeRespond = () => release.Task };
        var client = new PushoverClient(new TestHttpClientFactory(handler));

        // Act
        var sends = Enumerable.Range(0, 4).Select(_ => client.SendMessageAsync(CreateFields(), CancellationToken.None)).ToArray();
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Requests.Count == 2);
        release.SetResult();
        await Task.WhenAll(sends);

        // Assert
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(2, handler.MaximumConcurrentRequests);
    }
}
