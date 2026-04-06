using System.IdentityModel.Tokens.Jwt;
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}v4/space-accesses?limit=999");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var jsonDocument = JsonDocument.Parse(responseBody);

            var groupUids = new List<string>();
            foreach (var item in jsonDocument.RootElement.GetProperty("data").EnumerateArray())
            {
                var groupUid = item.GetProperty("attributes").GetProperty("group_uid").GetString();
                if (groupUid is not null)
                    groupUids.Add(groupUid);
            }

            var chargerTasks = groupUids.Select(uid => GetChargersForGroupAsync(uid, cancellationToken));
            var results = await Task.WhenAll(chargerTasks);
            return results.SelectMany(c => c).ToArray();
        }, cancellationToken);
    }

    private async Task<ChargerInformation[]> GetChargersForGroupAsync(string groupUid, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}perseus/organizations/{groupUid}/chargers?limit=50&offset=0&include=charger_info,charger_config,charger_status&filters=[]";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chargersResponse = JsonSerializer.Deserialize<ChargersResponse>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return chargersResponse?.Data?.Select(c => new ChargerInformation
        {
            Id = c.Id,
            Name = c.Attributes?.Name,
            Model = c.Attributes?.Model,
            Status = c.Attributes?.Status ?? 0,
            SerialNumber = c.Attributes?.SerialNumber,
        }).ToArray() ?? [];
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
        if (_token is null || _tokenExpiration < DateTimeOffset.UtcNow)
        {
            var authHeaderValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_email}:{_password}"));

            using var request = new HttpRequestMessage(HttpMethod.Get, AuthUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeaderValue);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var authResponse = JsonSerializer.Deserialize<AuthResponse>(responseBody);

            var token = authResponse?.Jwt;
            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtToken = tokenHandler.ReadJwtToken(token);

            _token = token;
            _tokenExpiration = jwtToken.Payload.Expiration.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(jwtToken.Payload.Expiration.Value)
                : DateTimeOffset.MinValue;
        }

        try
        {
            return await action();
        }
        catch
        {
            _token = null;
            _tokenExpiration = DateTimeOffset.MinValue;
            throw;
        }
    }
}
