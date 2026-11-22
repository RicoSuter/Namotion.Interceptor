using System.Globalization;
using System.Text;
using Namotion.Devices.Tesla.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;

namespace Namotion.Devices.Tesla;

/// <summary>A vehicle found while connecting an account.</summary>
public sealed record TeslaVehicleSummary(string Vin, string? DisplayName, TeslaVehicleState? State)
{
    /// <summary>The last six characters of the VIN, as the vehicle title shows them.</summary>
    public string VinSuffix => TeslaValues.VinSuffix(Vin);
}

/// <summary>
/// The result of connecting an account: the rotated refresh token to store and the vehicles found.
/// <see cref="object.ToString"/> leaves out the refresh token.
/// </summary>
/// <param name="RefreshToken">The refresh token to store; Tesla has replaced the given one.</param>
/// <param name="Email">The email of the account, or null.</param>
/// <param name="Vehicles">The vehicles of the account; empty when <paramref name="VehicleListError"/> is set.</param>
/// <param name="VehicleListError">Why the vehicles could not be listed, or null. The refresh token is valid nevertheless.</param>
public sealed record TeslaConnection(string RefreshToken, string? Email, IReadOnlyList<TeslaVehicleSummary> Vehicles, string? VehicleListError = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Email = {Email}, Vehicles = {Vehicles.Count}, VehicleListError = {VehicleListError}");
        return true;
    }
}

/// <summary>
/// A started browser login: open <see cref="Url"/>, then exchange the code of the pasted callback with <see cref="CodeVerifier"/>.
/// <see cref="object.ToString"/> leaves out the code verifier.
/// </summary>
public sealed record TeslaLogin(string Url, string State, string CodeVerifier)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Url = {Url}, State = {State}");
        return true;
    }
}

/// <summary>
/// Connects a Tesla account for the setup: checks a refresh token or a browser login and lists the vehicles. Never wakes a vehicle.
/// </summary>
/// <remarks>
/// Tesla replaces the given refresh token, so store <see cref="TeslaConnection.RefreshToken"/>, which is the only valid one afterwards.
/// </remarks>
public static class TeslaAccountConnector
{
    /// <summary>Starts a browser login; open <see cref="TeslaLogin.Url"/> and pass the pasted callback to <see cref="ConnectWithLoginAsync(TeslaLogin, string, TimeProvider, CancellationToken)"/>.</summary>
    public static TeslaLogin CreateLogin() => TeslaAuthClient.CreateLogin();

    /// <summary>
    /// Refreshes the pasted refresh token and lists the vehicles. Cancellation stops only the listing, so that the rotated
    /// refresh token is always returned.
    /// </summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the refresh token.</exception>
    /// <exception cref="TeslaApiException">Tesla failed.</exception>
    public static async Task<TeslaConnection> ConnectWithRefreshTokenAsync(string refreshToken, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var httpClient = TeslaHttp.CreateClient();
        return await ConnectWithRefreshTokenAsync(httpClient, refreshToken, timeProvider, cancellationToken);
    }

    /// <summary>
    /// Exchanges the code of the pasted callback address of <paramref name="login"/> and lists the vehicles. Cancellation
    /// stops only the listing, so that the refresh token is always returned.
    /// </summary>
    /// <exception cref="FormatException">The pasted text holds an error from Tesla, has no code or belongs to another login.</exception>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the code.</exception>
    /// <exception cref="TeslaApiException">Tesla failed.</exception>
    public static async Task<TeslaConnection> ConnectWithLoginAsync(TeslaLogin login, string pastedCallback, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var httpClient = TeslaHttp.CreateClient();
        return await ConnectWithLoginAsync(httpClient, login, pastedCallback, timeProvider, cancellationToken);
    }

    internal static async Task<TeslaConnection> ConnectWithRefreshTokenAsync(HttpClient httpClient, string refreshToken, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // Not canceled: Tesla may already have rotated the refresh token, and only the response holds the new one. The
        // HTTP client timeout bounds it.
        var tokens = await new TeslaAuthClient(httpClient).RefreshAsync(refreshToken.Trim(), timeProvider, CancellationToken.None);
        return await ListVehiclesAsync(httpClient, tokens, timeProvider, cancellationToken);
    }

    internal static async Task<TeslaConnection> ConnectWithLoginAsync(HttpClient httpClient, TeslaLogin login, string pastedCallback, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var code = TeslaAuthClient.ParseCallbackCode(pastedCallback, login.State);
        // Not canceled, like the refresh: the code is used up once it is sent.
        var tokens = await new TeslaAuthClient(httpClient).ExchangeCodeAsync(code, login.CodeVerifier, timeProvider, CancellationToken.None);
        return await ListVehiclesAsync(httpClient, tokens, timeProvider, cancellationToken);
    }

    private static async Task<TeslaConnection> ListVehiclesAsync(HttpClient httpClient, TeslaTokens tokens, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        TeslaProductDto[] products;
        try
        {
            products = await new TeslaOwnerApiClient(httpClient, timeProvider, NullLogger.Instance).GetVehicleProductsAsync(tokens.AccessToken, cancellationToken);
        }
        catch (Exception exception)
        {
            // Tesla has already replaced the given refresh token, so the rotated one is returned to be stored, also
            // when the listing was canceled.
            var error = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "Listing the vehicles was canceled."
                : exception.Message;
            return new TeslaConnection(tokens.RefreshToken, tokens.Email, [], error);
        }

        var vehicles = products
            .Where(product => !string.IsNullOrEmpty(product.Vin))
            .Select(product => new TeslaVehicleSummary(
                product.Vin!,
                TeslaValues.Text(product.DisplayName),
                TeslaValues.Enum<TeslaVehicleState>(product.State, NullLogger.Instance)))
            .ToArray();
        return new TeslaConnection(tokens.RefreshToken, tokens.Email, vehicles);
    }
}
