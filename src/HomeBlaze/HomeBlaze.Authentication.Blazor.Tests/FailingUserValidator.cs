using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// While <see cref="IsRefusing"/>, refuses to store a user who has a password and need not change it, which fails the
/// last save of setting a first password after the password itself was saved.
/// </summary>
internal sealed class FailingUserValidator : IUserValidator<ApplicationUser>
{
    public const string Error = "The user could not be stored.";

    private volatile bool _isRefusing;

    public bool IsRefusing
    {
        get => _isRefusing;
        set => _isRefusing = value;
    }

    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(IsRefusing && user.PasswordHash is not null && !user.RequiresPasswordChange()
            ? IdentityResult.Failed(new IdentityError { Code = nameof(FailingUserValidator), Description = Error })
            : IdentityResult.Success);
}
