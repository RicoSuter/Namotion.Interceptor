using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HomeBlaze.Authentication.Data;

internal sealed class AuthenticationDbContextFactory : IDesignTimeDbContextFactory<AuthenticationDbContext>
{
    public AuthenticationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuthenticationDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
