using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Namotion.Devices.Tesla.Client.Dto;

namespace Namotion.Devices.Tesla.Client;

/// <summary>
/// Reads the unofficial Tesla Owner API. Only <see cref="WakeUpAsync"/> wakes a vehicle.
/// </summary>
internal sealed class TeslaOwnerApiClient
{
    private const string BaseAddress = "https://owner-api.teslamotors.com/api/1/";
    private const string VehicleDataEndpoints =
        "charge_state%3Bclimate_state%3Bclosures_state%3Bdrive_state%3Bgui_settings%3Blocation_data%3Bvehicle_config%3Bvehicle_state%3Bvehicle_data_combo";
    private static readonly TimeSpan ExceededLimitRetryAfter = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    /// <param name="httpClient">A client that uses HTTP/2 and TLS 1.3, which Tesla requires.</param>
    /// <param name="timeProvider">The clock for a Retry-After date.</param>
    public TeslaOwnerApiClient(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    /// <summary>Reads the vehicles of the account with their state and cached snapshot. Never wakes a vehicle.</summary>
    /// <exception cref="TeslaApiException">Tesla answered with an error.</exception>
    public async Task<TeslaProductDto[]> GetVehicleProductsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var json = await GetAsync("products", accessToken, cancellationToken)
            ?? throw new TeslaApiException(HttpStatusCode.RequestTimeout, "products unavailable");
        var response = JsonSerializer.Deserialize(json, TeslaJsonContext.Default.TeslaResponseTeslaProductDtoArray);
        return response?.Response?.Where(product => product.VehicleId is not null).ToArray() ?? [];
    }

    /// <summary>Reads all data of an awake vehicle, or null when it is asleep or offline. Never wakes a vehicle.</summary>
    /// <exception cref="TeslaApiException">Tesla answered with an error.</exception>
    public async Task<TeslaVehicleDataDto?> GetVehicleDataAsync(string accessToken, long vehicleId, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"vehicles/{vehicleId}/vehicle_data?endpoints={VehicleDataEndpoints}", accessToken, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize(json, TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)?.Response;
    }

    /// <summary>Asks the vehicle to wake up. Returns immediately; the vehicle comes online within about a minute.</summary>
    /// <exception cref="TeslaApiException">Tesla answered with an error.</exception>
    public async Task WakeUpAsync(string accessToken, long vehicleId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"vehicles/{vehicleId}/wake_up", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <returns>The JSON, or null when Tesla answers 408 (vehicle asleep or offline).</returns>
    private async Task<string?> GetAsync(string path, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string accessToken)
    {
        var request = _httpClient.CreateRequest(method, BaseAddress + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = ReadError(await response.Content.ReadAsStringAsync(cancellationToken));
        TimeSpan? retryAfter = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => TeslaHttp.GetRetryAfter(response, _timeProvider),
            HttpStatusCode.Forbidden when error?.Contains("EXCEEDED_LIMIT", StringComparison.Ordinal) == true => ExceededLimitRetryAfter,
            _ => null
        };

        throw new TeslaApiException(response.StatusCode, error, retryAfter);
    }

    /// <returns>The error field of a JSON body, or null. Other bodies are never returned, so that they cannot reach a log.</returns>
    private static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
