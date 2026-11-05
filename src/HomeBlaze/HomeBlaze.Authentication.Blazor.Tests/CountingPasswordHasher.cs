using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>Identity's password hasher, counting how often a password is verified.</summary>
internal sealed class CountingPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new();
    private int _verificationCount;

    public int VerificationCount => Volatile.Read(ref _verificationCount);

    public string HashPassword(ApplicationUser user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _verificationCount);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
