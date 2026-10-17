using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HomeBlaze.History.Import.Influx;

/// <summary>
/// Reads one series at a time from an InfluxDB 2 bucket through the Flux query API.
/// </summary>
internal sealed class InfluxClient(HttpClient httpClient, string organization, string bucket) : IDisposable
{
    /// <summary>
    /// Streams every point of <paramref name="measurement"/>/<paramref name="field"/> in
    /// [<paramref name="from"/>, <paramref name="to"/>), across all tag sets. Points are not ordered across
    /// tag sets: the store routes by timestamp and ignores repeated instants, so order does not matter.
    /// </summary>
    public IEnumerable<FluxPoint> ReadSeries(
        string measurement, string field, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var query =
            $"from(bucket: {Quote(bucket)})\n" +
            $"  |> range(start: {Time(from)}, stop: {Time(to)})\n" +
            $"  |> filter(fn: (r) => r._measurement == {Quote(measurement)} and r._field == {Quote(field)})\n" +
            "  |> keep(columns: [\"_time\", \"_value\"])\n";

        var body = JsonSerializer.Serialize(new FluxRequest(query), FluxJsonContext.Default.FluxRequest);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/v2/query?org={Uri.EscapeDataString(organization)}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/csv"));

        using var response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using var errorReader = new StreamReader(response.Content.ReadAsStream(cancellationToken));
            throw new InvalidOperationException(
                $"InfluxDB returned {(int)response.StatusCode} for {measurement}/{field}: {errorReader.ReadToEnd()}");
        }

        using var reader = new StreamReader(response.Content.ReadAsStream(cancellationToken), Encoding.UTF8);
        foreach (var point in FluxCsvReader.Read(reader, cancellationToken))
        {
            yield return point;
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
