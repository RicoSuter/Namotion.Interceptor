using HomeBlaze.Authentication;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>Who a page of <see cref="PlaywrightFixture.CreatePageAsync(SignedInAs, Microsoft.Playwright.BrowserNewContextOptions?)"/> is signed in as.</summary>
public sealed class SignedInAs
{
    private SignedInAs(string? userName)
    {
        UserName = userName;
    }

    /// <summary>A visitor who is not signed in.</summary>
    public static SignedInAs None { get; } = new(null);

    /// <summary>The seeded administrator, a full administrator, signed in once for the whole fixture.</summary>
    public static SignedInAs Admin { get; } = new(AdminSeeding.AdminUserName);

    /// <summary>The name of the person, or null for a visitor who is not signed in.</summary>
    public string? UserName { get; }

    /// <summary>A person of <see cref="PlaywrightFixture.AddPersonAsync"/>, signed in through the sign-in dialog.</summary>
    public static SignedInAs User(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        return new SignedInAs(userName);
    }
}
