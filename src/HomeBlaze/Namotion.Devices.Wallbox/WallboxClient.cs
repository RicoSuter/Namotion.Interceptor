using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Namotion.Devices.Wallbox.Model;

namespace Namotion.Devices.Wallbox;

public class WallboxClient
{
    private readonly HttpClient _httpClient;
    private readonly string _email;
    private readonly string _password;

    private string? _token;
    private DateTimeOffset _tokenExpiration;
    private string? _refreshToken;
    private DateTimeOffset _refreshTokenExpiration;

    private const string BaseUrl = "https://api.wall-box.com/";
    private const string AuthUrl = "https://user-api.wall-box.com/users/signin";

    public WallboxClient(IHttpClientFactory httpClientFactory, string email, string password)
    {
        _httpClient = httpClientFactory.CreateClient();
        _email = email;
        _password = password;
    }

    public async Task<ChargerInformation[]> GetChargersAsync(CancellationToken cancellationToken)
    {
        return await AuthenticateAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}v3/chargers/groups");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var jsonDocument = JsonDocument.Parse(responseBody);

            var chargers = new List<ChargerInformation>();
            foreach (var group in jsonDocument.RootElement.GetProperty("result").GetProperty("groups").EnumerateArray())
            {
                foreach (var charger in group.GetProperty("chargers").EnumerateArray())
                {
                    chargers.Add(new ChargerInformation
                    {
                        Id = charger.TryGetProperty("id", out var id) ? id.ToString() : null,
                        Name = charger.TryGetProperty("name", out var name) ? name.GetString() : null,
                        SerialNumber = charger.TryGetProperty("serialNumber", out var sn)
                            ? sn.GetString()
                            : charger.TryGetProperty("id", out var idFallback) ? idFallback.ToString() : null,
                    });
                }
            }

            return chargers.ToArray();
        }, cancellationToken);
    }

    internal async Task<ChargerStatusResponse> GetChargerStatusAsync(string chargerId, CancellationToken cancellationToken)
    {
        return await AuthenticateAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}chargers/status/{chargerId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<ChargerStatusResponse>(responseBody) ?? new ChargerStatusResponse();
        }, cancellationToken);
    }

    internal async Task<ChargingSessionData[]> GetChargingSessionsAsync(
        int groupId, int chargerId, DateTimeOffset startTime, DateTimeOffset endTime,
        CancellationToken cancellationToken)
    {
        return await AuthenticateAsync(async () =>
        {
            var filters = $"{{\"filters\":[" +
                $"{{\"field\":\"start_time\",\"operator\":\"gte\",\"value\":{startTime.ToUnixTimeSeconds()}}}," +
                $"{{\"field\":\"start_time\",\"operator\":\"lt\",\"value\":{endTime.ToUnixTimeSeconds()}}}," +
                $"{{\"field\":\"charger_id\",\"operator\":\"eq\",\"value\":{chargerId}}}]}}";

            var url = $"{BaseUrl}v4/groups/{groupId}/charger-charging-sessions" +
                $"?filters={Uri.EscapeDataString(filters)}&fields[charger_charging_session]=&limit=10000&offset=0";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<ChargingSessionsResponse>(responseBody)?.Data ?? [];
        }, cancellationToken);
    }

    internal async Task SetMaxChargingCurrentAsync(string chargerId, int current, CancellationToken cancellationToken)
    {
        await ControlChargerAsync(chargerId, "maxChargingCurrent", current, cancellationToken);
    }

    internal async Task LockAsync(string chargerId, CancellationToken cancellationToken)
    {
        await ControlChargerAsync(chargerId, "locked", 1, cancellationToken);
    }

    internal async Task UnlockAsync(string chargerId, CancellationToken cancellationToken)
    {
        await ControlChargerAsync(chargerId, "locked", 0, cancellationToken);
    }

    internal async Task ResumeAsync(string chargerId, CancellationToken cancellationToken)
    {
        await PerformRemoteActionAsync(chargerId, 1, cancellationToken);
    }

    internal async Task PauseAsync(string chargerId, CancellationToken cancellationToken)
    {
        await PerformRemoteActionAsync(chargerId, 2, cancellationToken);
    }

    internal async Task RebootAsync(string chargerId, CancellationToken cancellationToken)
    {
        await PerformRemoteActionAsync(chargerId, 3, cancellationToken);
    }

    internal async Task UpdateFirmwareAsync(string chargerId, CancellationToken cancellationToken)
    {
        await PerformRemoteActionAsync(chargerId, 5, cancellationToken);
    }

    internal async Task SetEnergyPriceAsync(string chargerId, decimal price, CancellationToken cancellationToken)
    {
        await ControlChargerConfigAsync(chargerId, new Dictionary<string, object> { ["energy_price"] = price }, cancellationToken);
    }

    internal async Task SetIcpMaxCurrentAsync(string chargerId, int current, CancellationToken cancellationToken)
    {
        await ControlChargerConfigAsync(chargerId, new Dictionary<string, object> { ["icp_max_current"] = current }, cancellationToken);
    }

    internal async Task SetEcoSmartAsync(string chargerId, WallboxEcoSmartMode mode, CancellationToken cancellationToken)
    {
        await AuthenticateAsync<object?>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}v4/chargers/{chargerId}/eco-smart");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var data = new
            {
                data = new
                {
                    attributes = new
                    {
                        percentage = 100,
                        enabled = mode != WallboxEcoSmartMode.Disabled ? 1 : 0,
                        mode = (int)mode
                    },
                    type = "eco_smart"
                }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return null;
        }, cancellationToken);
    }

    private async Task ControlChargerAsync(string chargerId, string key, object value, CancellationToken cancellationToken)
    {
        await AuthenticateAsync<object?>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}v2/charger/{chargerId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, object> { [key] = value }),
                Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return null;
        }, cancellationToken);
    }

    private async Task ControlChargerConfigAsync(string chargerId, Dictionary<string, object> data, CancellationToken cancellationToken)
    {
        await AuthenticateAsync<object?>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}chargers/config/{chargerId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return null;
        }, cancellationToken);
    }

    // Remote action codes for POST /v3/chargers/{id}/remote-action.
    // 1 = Resume, 2 = Pause, 3 = Reboot, 4 = Factory Reset, 5 = Update Firmware, 9 = Resume Schedule/EcoSmart
    // See https://github.com/SKB-CGN/wallbox#variables-for-action-value
    // See https://github.com/home-assistant/core/blob/dev/homeassistant/components/wallbox/coordinator.py
    private async Task PerformRemoteActionAsync(string chargerId, int action, CancellationToken cancellationToken)
    {
        await AuthenticateAsync<object?>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}v3/chargers/{chargerId}/remote-action");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, object> { ["action"] = action }),
                Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return null;
        }, cancellationToken);
    }

    private async Task<T> AuthenticateAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            return await action();
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Token may have been revoked server-side — force re-auth and retry once
            _token = null;
            _tokenExpiration = DateTimeOffset.MinValue;
            _refreshToken = null;
            _refreshTokenExpiration = DateTimeOffset.MinValue;

            await EnsureAuthenticatedAsync(cancellationToken);
            return await action();
        }
        catch
        {
            _token = null;
            _tokenExpiration = DateTimeOffset.MinValue;
            throw;
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _tokenExpiration > DateTimeOffset.UtcNow)
            return;

        // Try refresh token first (avoids sending credentials)
        if (_refreshToken is not null && _refreshTokenExpiration > DateTimeOffset.UtcNow)
        {
            using var refreshRequest = new HttpRequestMessage(HttpMethod.Get, $"{AuthUrl.Replace("users/signin", "users/refresh-token")}");
            refreshRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _refreshToken);
            refreshRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            refreshRequest.Headers.Add("Partner", "wallbox");

            var refreshResponse = await _httpClient.SendAsync(refreshRequest, cancellationToken);
            if (refreshResponse.IsSuccessStatusCode)
            {
                ApplyAuthResponse(await refreshResponse.Content.ReadAsStringAsync(cancellationToken));
                return;
            }

            // Refresh failed — fall through to full signin
        }

        // Full signin with credentials
        var authHeaderValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_email}:{_password}"));

        using var request = new HttpRequestMessage(HttpMethod.Get, AuthUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Partner", "wallbox");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        ApplyAuthResponse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private void ApplyAuthResponse(string responseBody)
    {
        var jsonDocument = JsonDocument.Parse(responseBody);
        var attributes = jsonDocument.RootElement.GetProperty("data").GetProperty("attributes");

        _token = attributes.GetProperty("token").GetString();
        var ttlMs = attributes.GetProperty("ttl").GetInt64();
        _tokenExpiration = DateTimeOffset.UtcNow.AddMilliseconds(ttlMs);

        if (attributes.TryGetProperty("refresh_token", out var refreshToken))
            _refreshToken = refreshToken.GetString();

        if (attributes.TryGetProperty("refresh_token_ttl", out var refreshTtl))
            _refreshTokenExpiration = DateTimeOffset.UtcNow.AddMilliseconds(refreshTtl.GetInt64());
    }
}
