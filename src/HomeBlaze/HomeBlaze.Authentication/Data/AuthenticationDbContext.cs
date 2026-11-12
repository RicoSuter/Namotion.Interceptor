using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HomeBlaze.Authentication.Data;

/// <summary>The authentication database: Identity's tables with <see cref="ApplicationUser"/> and <see cref="ApplicationRole"/>.</summary>
public class AuthenticationDbContext(DbContextOptions<AuthenticationDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, string>(options)
{
}
