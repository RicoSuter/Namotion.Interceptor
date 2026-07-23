using System.Text.Json;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class GetPropertyHistoryToolTests
{
    private static async Task<JsonElement> InvokeAsync(object inputObject)
    {
        var (_, _, factory) = HomeBlazeMcpToolProviderTests.CreateTestSetup(isReadOnly: true);
        var tool = factory.CreateTools().First(candidate => candidate.Name == "get_property_history");
        var input = JsonSerializer.SerializeToElement(inputObject);
        var result = await tool.Handler(input, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result);
    }

    [Fact]
    public async Task WhenPathsMissing_ThenError()
    {
        // Act
        var json = await InvokeAsync(new { from = "2026-06-24T00:00:00Z" });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenFromMissing_ThenError()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/a/Value" } });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenPathsContainsOnlyNonStringElements_ThenStructuredErrorNotException()
    {
        // Act - a non-string element must not throw; it yields the structured "at least one path" error.
        var json = await InvokeAsync(new
        {
            paths = new object[] { 123 },
            from = "2026-06-24T00:00:00Z"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("at least one path", error.GetString());
    }

    [Fact]
    public async Task WhenPathsMixesStringAndNonStringElements_ThenNonStringIgnoredAndNoError()
    {
        // Act - the non-string element is skipped and the valid string path drives a normal (empty) result.
        var json = await InvokeAsync(new
        {
            paths = new object[] { "/a/Value", 123 },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.True(json.TryGetProperty("/a/Value", out _));
    }

    [Fact]
    public async Task WhenAggregationUnknown_ThenErrorWithAvailableSet()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            aggregation = "bogus"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
        var available = json.GetProperty("available").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("TimeWeightedAverage", available);
        Assert.Contains("Last", available);
    }

    [Fact]
    public async Task WhenBucketInvalid_ThenError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = "5x"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenValidRawQueryWithNoStores_ThenPerPathEntriesWithEmptyPoints()
    {
        // Arrange - default aggregation Last (AlwaysAvailable), no stores -> empty points, not an error.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value", "/b/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        foreach (var path in new[] { "/a/Value", "/b/Value" })
        {
            Assert.True(json.TryGetProperty(path, out var entry));
            Assert.True(entry.TryGetProperty("value_type", out _));     // present (null when unresolved)
            Assert.Empty(entry.GetProperty("points").EnumerateArray());
            Assert.False(entry.GetProperty("truncated").GetBoolean());
        }
    }

    [Fact]
    public async Task WhenBucketAndCaseInsensitiveCountAggregation_ThenNoError()
    {
        // Arrange - "count" normalizes to Count (AlwaysAvailable); bucket "5m" parses; no stores -> empty, no error.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            bucket = "5m",
            aggregation = "count"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.True(json.TryGetProperty("/a/Value", out _));
    }
}
