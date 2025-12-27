using System.Web;
using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace HomeBlaze.Authorization.Endpoints;

/// <summary>
/// Minimal API endpoints for authentication (login/logout).
/// Uses form POST for proper browser cookie handling.
/// </summary>
public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Login endpoint - accepts form POST, redirects on success/failure
        endpoints.MapPost("/api/auth/login", async (
            HttpContext context,
            [FromForm] string username,
            [FromForm] string password,
            [FromForm] bool? rememberMe,
            [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByNameAsync(username);
            if (user == null)
            {
                return Results.Redirect($"/login?ErrorMessage={HttpUtility.UrlEncode("Invalid username or password.")}");
            }

            // Check if user needs to set password
            var hasPassword = await userManager.HasPasswordAsync(user);
            if (!hasPassword || user.MustChangePassword)
            {
                // Include flag to indicate if user has existing (temp) password
                var requiresTempPassword = hasPassword ? "true" : "false";
                return Results.Redirect($"/set-password/{user.Id}?RequiresTempPassword={requiresTempPassword}");
            }

            var result = await signInManager.PasswordSignInAsync(
                username,
                password,
                rememberMe ?? false,
                lockoutOnFailure: true);

            if (result.Succeeded)
            {
                user.LastLoginAt = DateTime.UtcNow;
                await userManager.UpdateAsync(user);
                return Results.Redirect(returnUrl ?? "/");
            }

            if (result.IsLockedOut)
            {
                return Results.Redirect($"/login?ErrorMessage={HttpUtility.UrlEncode("Account is locked. Please try again later.")}");
            }

            return Results.Redirect($"/login?ErrorMessage={HttpUtility.UrlEncode("Invalid username or password.")}");
        });

        // Set password endpoint - accepts form POST
        // Security: Only allows password setting for users who:
        // 1. Have MustChangePassword = true (admin initiated password set/reset)
        // 2. Have no password yet (initial user setup)
        endpoints.MapPost("/api/auth/set-password", async (
            [FromForm] string userId,
            [FromForm] string? tempPassword,
            [FromForm] string password,
            [FromForm] string? confirmPassword,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return Results.Redirect($"/set-password/{userId}?ErrorMessage={HttpUtility.UrlEncode("User not found.")}");
            }

            // Security check: Only allow password setting if MustChangePassword is true
            if (!user.MustChangePassword)
            {
                return Results.Redirect($"/login?ErrorMessage={HttpUtility.UrlEncode("Password change not required. Use the account page to change your password.")}");
            }

            if (password != confirmPassword)
            {
                return Results.Redirect($"/set-password/{userId}?ErrorMessage={HttpUtility.UrlEncode("Passwords do not match.")}");
            }

            var hasPassword = await userManager.HasPasswordAsync(user);
            IdentityResult result;

            if (hasPassword)
            {
                // User has a temp password from admin reset - verify it first
                if (string.IsNullOrEmpty(tempPassword))
                {
                    return Results.Redirect($"/set-password/{userId}?ErrorMessage={HttpUtility.UrlEncode("Current temporary password is required.")}");
                }

                // Verify temp password before allowing change
                var isValidTempPassword = await userManager.CheckPasswordAsync(user, tempPassword);
                if (!isValidTempPassword)
                {
                    return Results.Redirect($"/set-password/{userId}?ErrorMessage={HttpUtility.UrlEncode("Invalid temporary password.")}");
                }

                result = await userManager.ChangePasswordAsync(user, tempPassword, password);
            }
            else
            {
                // User has no password yet (initial setup)
                result = await userManager.AddPasswordAsync(user, password);
            }

            if (!result.Succeeded)
            {
                var errorMessage = result.GetErrorMessage(" ");
                return Results.Redirect($"/set-password/{userId}?ErrorMessage={HttpUtility.UrlEncode(errorMessage)}");
            }

            user.MustChangePassword = false;
            user.LastLoginAt = DateTime.UtcNow;
            await userManager.UpdateAsync(user);

            await signInManager.SignInAsync(user, isPersistent: false);

            return Results.Redirect("/");
        });

        // Logout endpoint - accepts form POST or GET
        endpoints.MapPost("/api/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/");
        });

        endpoints.MapGet("/api/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/");
        });

        // Change password endpoint - requires current password
        endpoints.MapPost("/api/auth/change-password", async (
            HttpContext context,
            [FromForm] string currentPassword,
            [FromForm] string newPassword,
            [FromForm] string? confirmPassword,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user == null)
            {
                return Results.Redirect("/account?ErrorMessage=User%20not%20found");
            }

            if (newPassword != confirmPassword)
            {
                return Results.Redirect("/account?ErrorMessage=Passwords%20do%20not%20match");
            }

            var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
            if (!result.Succeeded)
            {
                var errorMessage = result.GetErrorMessage(" ");
                return Results.Redirect($"/account?ErrorMessage={HttpUtility.UrlEncode(errorMessage)}");
            }

            return Results.Redirect("/account?SuccessMessage=Password%20changed%20successfully");
        }).RequireAuthorization();

        return endpoints;
    }
}
