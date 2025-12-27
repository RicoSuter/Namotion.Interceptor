using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HomeBlaze.Authorization.Data;

/// <summary>
/// Entity Framework database context for authorization data.
/// Includes Identity tables plus custom role hierarchy and external mapping tables.
/// </summary>
public class AuthorizationDbContext : IdentityDbContext<ApplicationUser>
{
    /// <summary>
    /// Role hierarchy definitions.
    /// </summary>
    public DbSet<RoleComposition> RoleCompositions { get; set; } = null!;

    /// <summary>
    /// External provider role mappings.
    /// </summary>
    public DbSet<ExternalRoleMapping> ExternalRoleMappings { get; set; } = null!;

    public AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // RoleComposition configuration
        builder.Entity<RoleComposition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RoleName);
            entity.HasIndex(e => new { e.RoleName, e.IncludesRole }).IsUnique();
        });

        // ExternalRoleMapping configuration
        builder.Entity<ExternalRoleMapping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Provider, e.ExternalRole });
            entity.HasIndex(e => new { e.Provider, e.ExternalRole, e.InternalRole }).IsUnique();
        });
    }
}
