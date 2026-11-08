using System.Text.Json.Serialization;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The account endpoints, which the dialogs of the app post their forms to, as only a request, not a circuit, can set the
/// session cookie. Each answers in JSON: <c>{ "redirect": url }</c> to load next, <c>{ "error": message }</c> or
/// <c>{ "errors": [messages] }</c> with 400.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>The path of the endpoint that signs a person in with their user name and password.</summary>
    public const string SignInPath = "/account/sign-in";

    /// <summary>The path of the endpoint that sets or changes the password of the signed-in person.</summary>
    public const string PasswordPath = "/account/password";

    /// <summary>The path of the endpoint that redeems a setup code, and of the page of the app that shows its dialog.</summary>
    public const string SetupPath = "/account/setup";

    /// <summary>The path of the endpoint that sets the display name of the signed-in person.</summary>
    public const string DisplayNamePath = "/account/display-name";

    /// <summary>The path of the endpoint that signs a person out.</summary>
    public const string LogoutPath = "/account/logout";

    private const string TokenRefusal = "The form is outdated. Reload the page and try again.";

    /// <summary>
    /// Maps the account endpoints, which need a valid antiforgery token, otherwise they answer 400 and change nothing:
    /// <list type="bullet">
    /// <item><c>POST</c> <see cref="SignInPath"/> (<c>userName</c>, <c>password</c>, <c>rememberMe</c>, <c>returnUrl</c>),
    /// which redirects to the safe return URL, see <see cref="ReturnUrls.GetSafe"/>.</item>
    /// <item><c>POST</c> <see cref="PasswordPath"/> for a signed-in person (<c>currentPassword</c>, <c>newPassword</c>,
    /// <c>confirmPassword</c>, <c>returnUrl</c>), which redirects to the safe return URL, or to the root once it signed
    /// out an ended session, also with 401 when the session is refused before the endpoint.</item>
    /// <item><c>POST</c> <see cref="SetupPath"/> (<c>userName</c>, <c>code</c>, <c>newPassword</c>,
    /// <c>confirmPassword</c>), which redirects to the root.</item>
    /// <item><c>POST</c> <see cref="DisplayNamePath"/> for a signed-in person (<c>displayName</c>, <c>returnUrl</c>), which
    /// redirects to the safe return URL, or to the root once it signed out an ended session, also with 401 when the
    /// session is refused before the endpoint.</item>
    /// <item><c>POST</c> <see cref="LogoutPath"/>, which signs the person out and redirects to the root; a request that is
    /// not signed in, such as one whose session expired, is redirected without a token.</item>
    /// </list>
    /// All but the last are rate limited with <see cref="AuthenticationWebExtensions.LoginRateLimitPolicy"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapHomeBlazeAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(string.Empty);
        group.MapPost(SignInPath, SignInAsync).AllowAnonymous().RequireRateLimiting(AuthenticationWebExtensions.LoginRateLimitPolicy);
        group.MapPost(PasswordPath, ChangePasswordAsync).RequireAuthorization().RequireRateLimiting(AuthenticationWebExtensions.LoginRateLimitPolicy);
        // Before the page of the same path, which takes posts as well.
        group.MapPost(SetupPath, RedeemSetupCodeAsync).AllowAnonymous().RequireRateLimiting(AuthenticationWebExtensions.LoginRateLimitPolicy).WithOrder(-1);
        group.MapPost(DisplayNamePath, SetDisplayNameAsync).RequireAuthorization().RequireRateLimiting(AuthenticationWebExtensions.LoginRateLimitPolicy);
        group.MapPost(LogoutPath, LogoutAsync).AllowAnonymous();
        return group;
    }

    private static async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery, PasswordSignIn signIn)
    {
        if (await ReadValidFormAsync(context, antiforgery) is not { } form)
        {
            return Error(TokenRefusal);
        }

        var isPersistent = string.Equals(form["rememberMe"], "true", StringComparison.OrdinalIgnoreCase);
        var password = form["password"].ToString();
        if (!await signIn.SignInAsync(context, form["userName"].ToString(), password, isPersistent))
        {
            return Error(SignInRefusals.Message);
        }

        // Only an administrator without a password signs in with an empty one, and must set it first: the root shows the
        // dialog for it, while any other address answers 403.
        if (password.Length == 0)
        {
            return Redirect(GetRoot(context));
        }

        return Redirect(ReturnUrls.GetSafe(form["returnUrl"], context.Request.PathBase));
    }

    private static async Task<IResult> ChangePasswordAsync(HttpContext context, IAntiforgery antiforgery, PasswordChange passwordChange)
    {
        if (await ReadValidFormAsync(context, antiforgery) is not { } form)
        {
            return Error(TokenRefusal);
        }

        var (outcome, errors) = await passwordChange.ChangeAsync(
            context, form["currentPassword"].ToString(), form["newPassword"].ToString(), form["confirmPassword"].ToString());
        return outcome switch
        {
            PasswordChangeOutcome.Changed => Redirect(ReturnUrls.GetSafe(form["returnUrl"], context.Request.PathBase)),
            PasswordChangeOutcome.Refused => Errors(errors),
            _ => Redirect(GetRoot(context))
        };
    }

    private static async Task<IResult> RedeemSetupCodeAsync(HttpContext context, IAntiforgery antiforgery, SetupCodeSignIn setupCodeSignIn)
    {
        if (await ReadValidFormAsync(context, antiforgery) is not { } form)
        {
            return Error(TokenRefusal);
        }

        var (outcome, errors) = await setupCodeSignIn.RedeemAsync(
            context, form["userName"].ToString(), form["code"].ToString(), form["newPassword"].ToString(), form["confirmPassword"].ToString());
        return outcome switch
        {
            SetupCodeOutcome.SignedIn => Redirect(GetRoot(context)),
            SetupCodeOutcome.InvalidPassword => Errors(errors),
            _ => Error(SignInRefusals.Message)
        };
    }

    private static async Task<IResult> SetDisplayNameAsync(
        HttpContext context, IAntiforgery antiforgery, SignInManager<ApplicationUser> signInManager, UserStore userStore)
    {
        if (await ReadValidFormAsync(context, antiforgery) is not { } form)
        {
            return Error(TokenRefusal);
        }

        // The cookie is checked against the stored stamp only every few minutes, so a session that a reset or a lock ended
        // would otherwise still change the account.
        var user = await signInManager.ValidateSecurityStampAsync(context.User);
        if (user is null || user.IsLockedByAdministrator())
        {
            await signInManager.SignOutAsync();
            return Redirect(GetRoot(context));
        }

        var result = await userStore.SetOwnDisplayNameAsync(user.Id, form["displayName"].ToString(), context.RequestAborted);
        return result.Succeeded
            ? Redirect(ReturnUrls.GetSafe(form["returnUrl"], context.Request.PathBase))
            : Error(result.Error ?? "The display name could not be set.");
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery, SignInManager<ApplicationUser> signInManager)
    {
        var redirect = Results.LocalRedirect("~/");
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return redirect;
        }

        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        await signInManager.SignOutAsync();
        return redirect;
    }

    // The form when it is one and holds a valid antiforgery token; otherwise null.
    private static async Task<IFormCollection?> ReadValidFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        // The answers hold what a person typed wrong, so they must not be cached.
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
        {
            return null;
        }

        return await context.Request.ReadFormAsync(context.RequestAborted);
    }

    /// <summary>
    /// Answers a request to <see cref="PasswordPath"/> or <see cref="DisplayNamePath"/> that has no session, such as one whose
    /// cookie was refused, with <paramref name="statusCode"/> and the root to load, where the app signs in again, and
    /// returns true; returns false for any other request.
    /// </summary>
    internal static async Task<bool> TryAnswerWithoutSessionAsync(HttpContext context, int statusCode)
    {
        if (!context.Request.Path.Equals(PasswordPath) && !context.Request.Path.Equals(DisplayNamePath))
        {
            return false;
        }

        await TypedResults.Json(new AccountRedirect(GetRoot(context)), AccountJsonContext.Default.AccountRedirect, statusCode: statusCode).ExecuteAsync(context);
        return true;
    }

    private static string GetRoot(HttpContext context) => ReturnUrls.GetSafe(returnUrl: null, context.Request.PathBase);

    private static IResult Redirect(string url) =>
        TypedResults.Json(new AccountRedirect(url), AccountJsonContext.Default.AccountRedirect);

    private static IResult Error(string message) =>
        TypedResults.Json(new AccountError(message), AccountJsonContext.Default.AccountError, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Errors(IReadOnlyList<string> messages) =>
        TypedResults.Json(new AccountErrors(messages), AccountJsonContext.Default.AccountErrors, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Registers the services of the endpoints and of the dialogs that post to them.</summary>
    internal static void AddAccountEndpointServices(this IServiceCollection services)
    {
        services.AddScoped<PasswordSignIn>();
        services.AddScoped<PasswordChange>();
        services.AddScoped<SetupCodeSignIn>();
        services.AddSingleton<Components.AccountPasswords>();
        services.AddScoped<Components.RequestTransport>();
    }
}

internal sealed record AccountRedirect(string Redirect);

internal sealed record AccountError(string Error);

internal sealed record AccountErrors(IReadOnlyList<string> Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AccountRedirect))]
[JsonSerializable(typeof(AccountError))]
[JsonSerializable(typeof(AccountErrors))]
internal sealed partial class AccountJsonContext : JsonSerializerContext;
