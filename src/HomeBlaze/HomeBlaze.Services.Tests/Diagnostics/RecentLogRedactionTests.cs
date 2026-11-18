using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class RecentLogRedactionTests
{
    [Theory]
    [InlineData("http://admin:hunter2@192.168.1.10/status", "http://admin:***@192.168.1.10/status")]
    [InlineData("http://admin:averylongpassw\u2026", "http://admin:***\u2026")]
    [InlineData("GET https://api.example.com/v1?access_token=abc.def-123&page=2", "GET https://api.example.com/v1?access_token=***&page=2")]
    [InlineData("client_secret=s3cr3t; next", "client_secret=***; next")]
    [InlineData("Password: hunter2", "Password: ***")]
    [InlineData("{\"apiKey\":\"abc123\",\"name\":\"Kitchen\"}", "{\"apiKey\":\"***\",\"name\":\"Kitchen\"}")]
    [InlineData("api-key=xyz", "api-key=***")]
    [InlineData("Authorization: Bearer eyJhbGciOi.eyJzdWIi.c2lnbmF0dXJl", "Authorization: Bearer ***")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz==", "Authorization: Basic ***")]
    [InlineData("X-Auth-Token: abc", "X-Auth-Token: ***")]
    public void WhenTextHoldsSecret_ThenItIsMasked(string text, string expected)
    {
        // Act
        var redacted = RecentLogRedaction.Redact(text);

        // Assert
        Assert.Equal(expected, redacted);
    }

    [Theory]
    [InlineData("Speaker Kitchen went offline")]
    [InlineData("Token count: 5 of 10")]
    [InlineData("Request to http://192.168.1.10:8080/status failed")]
    [InlineData("Basic setup completed")]
    public void WhenTextHoldsNoSecret_ThenTheSameInstanceIsReturned(string text)
    {
        // Act
        var redacted = RecentLogRedaction.Redact(text);

        // Assert
        Assert.Same(text, redacted);
    }
}
