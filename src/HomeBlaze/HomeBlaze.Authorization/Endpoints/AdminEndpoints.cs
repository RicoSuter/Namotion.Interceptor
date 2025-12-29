using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Roles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HomeBlaze.Authorization.Endpoints;

/// <summary>
/// Admin API endpoints for user and role management.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin")
            .RequireAuthorization(policy => policy.RequireRole(DefaultRoles.Admin));

        // User Management
        admin.MapGet("/users", async (
            UserManager<ApplicationUser> userManager,
            [FromQuery] string? search,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20) =>
        {
            var query = userManager.Users.AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(u =>
                    u.UserName!.Contains(search) ||
                    u.Email!.Contains(search));
            }

            var total = await query.CountAsync();
            var users = await query
                .OrderBy(u => u.UserName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new UserDto
                {
                    Id = u.Id,
                    UserName = u.UserName!,
                    Email = u.Email,
                    LastLoginAt = u.LastLoginAt,
                    MustChangePassword = u.MustChangePassword,
                    LockoutEnd = u.LockoutEnd
                })
                .ToListAsync();

            // Get roles for each user
            foreach (var user in users)
            {
                var appUser = await userManager.FindByIdAsync(user.Id);
                if (appUser != null)
                {
                    user.Roles = (await userManager.GetRolesAsync(appUser)).ToList();
                }
            }

            return Results.Ok(new PagedResult<UserDto>
            {
                Items = users,
                Total = total,
                Page = page,
                PageSize = pageSize
            });
        });

        admin.MapGet("/users/{id}", async (
            string id,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user == null)
                return Results.NotFound();

            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(new UserDto
            {
                Id = user.Id,
                UserName = user.UserName!,
                Email = user.Email,
                LastLoginAt = user.LastLoginAt,
                MustChangePassword = user.MustChangePassword,
                LockoutEnd = user.LockoutEnd,
                Roles = roles.ToList()
            });
        });

        admin.MapPost("/users", async (
            [FromBody] CreateUserRequest request,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = new ApplicationUser
            {
                UserName = request.UserName,
                Email = request.Email,
                MustChangePassword = true
            };

            var result = await userManager.CreateAsync(user);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
            }

            if (request.Roles?.Any() == true)
            {
                await userManager.AddToRolesAsync(user, request.Roles);
            }

            return Results.Created($"/api/admin/users/{user.Id}", new { user.Id });
        });

        admin.MapPut("/users/{id}", async (
            string id,
            [FromBody] UpdateUserRequest request,
            UserManager<ApplicationUser> userManager,
            HttpContext context) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user == null)
                return Results.NotFound();

            // Prevent self-demotion from Admin
            var currentUserId = userManager.GetUserId(context.User);
            if (id == currentUserId && request.Roles != null && !request.Roles.Contains(DefaultRoles.Admin))
            {
                return Results.BadRequest(new { Error = "Cannot remove Admin role from yourself" });
            }

            if (request.Email != null)
                user.Email = request.Email;

            var updateResult = await userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                return Results.BadRequest(new { Errors = updateResult.Errors.Select(e => e.Description) });
            }

            if (request.Roles != null)
            {
                var currentRoles = await userManager.GetRolesAsync(user);
                await userManager.RemoveFromRolesAsync(user, currentRoles);
                await userManager.AddToRolesAsync(user, request.Roles);
            }

            return Results.Ok();
        });

        admin.MapDelete("/users/{id}", async (
            string id,
            UserManager<ApplicationUser> userManager,
            HttpContext context) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user == null)
                return Results.NotFound();

            // Prevent self-deletion
            var currentUserId = userManager.GetUserId(context.User);
            if (id == currentUserId)
            {
                return Results.BadRequest(new { Error = "Cannot delete yourself" });
            }

            // Check if this is the last admin
            var isAdmin = await userManager.IsInRoleAsync(user, DefaultRoles.Admin);
            if (isAdmin)
            {
                var adminUsers = await userManager.GetUsersInRoleAsync(DefaultRoles.Admin);
                if (adminUsers.Count <= 1)
                {
                    return Results.BadRequest(new { Error = "Cannot delete the last admin user" });
                }
            }

            var result = await userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
            }

            return Results.Ok();
        });

        admin.MapPost("/users/{id}/reset-password", async (
            string id,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user == null)
                return Results.NotFound();

            // Remove existing password and set MustChangePassword flag
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var tempPassword = Guid.NewGuid().ToString("N")[..12] + "Aa1!";
            var result = await userManager.ResetPasswordAsync(user, token, tempPassword);

            if (!result.Succeeded)
            {
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
            }

            user.MustChangePassword = true;
            await userManager.UpdateAsync(user);

            return Results.Ok(new { TempPassword = tempPassword });
        });

        admin.MapPost("/users/{id}/unlock", async (
            string id,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user == null)
                return Results.NotFound();

            var result = await userManager.SetLockoutEndDateAsync(user, null);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
            }

            return Results.Ok();
        });

        // Role Management
        admin.MapGet("/roles", async (RoleManager<IdentityRole> roleManager) =>
        {
            var roles = await roleManager.Roles
                .OrderBy(r => r.Name)
                .Select(r => new RoleDto { Id = r.Id, Name = r.Name! })
                .ToListAsync();

            return Results.Ok(roles);
        });

        admin.MapPost("/roles", async (
            [FromBody] CreateRoleRequest request,
            RoleManager<IdentityRole> roleManager) =>
        {
            var role = new IdentityRole(request.Name);
            var result = await roleManager.CreateAsync(role);

            if (!result.Succeeded)
            {
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
            }

            return Results.Created($"/api/admin/roles/{role.Id}", new { role.Id });
        });

        admin.MapDelete("/roles/{id}", async (
            string id,
            RoleManager<IdentityRole> roleManager) =>
        {
            var role = await roleManager.FindByIdAsync(id);
            if (role == null)
                return Results.NotFound();

            // Protect system roles
            var systemRoles = new[] { DefaultRoles.Admin, DefaultRoles.Anonymous, DefaultRoles.Guest, DefaultRoles.User };
            if (systemRoles.Contains(role.Name))
            {
                return Results.BadRequest(new { Error = "Cannot delete system roles" });
            }

            var result = await roleManager.DeleteAsync(role);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });
            }

            return Results.Ok();
        });

        return endpoints;
    }
}
