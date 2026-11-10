using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class SetupCodesTests
{
    [Fact]
    public async Task WhenACodeIsRedeemedTwice_ThenTheSecondRedeemFails()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);

        // Act
        var first = await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time);
        var second = await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time);

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task WhenACodeIsOlderThan24Hours_ThenItFails()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);
        graph.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        // Act
        var redeemed = await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time);

        // Assert
        Assert.False(redeemed);
    }

    [Fact]
    public async Task WhenACodeIsTypedInLowerCaseWithSpaces_ThenItIsRedeemed()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);

        // Act
        var redeemed = await SetupCodes.TryRedeemAsync(graph.UserManager, user, $" {code.ToLowerInvariant()} ", graph.Time);

        // Assert
        Assert.True(redeemed);
    }

    [Fact]
    public async Task WhenAWrongCodeIsRedeemed_ThenItFailsAndCountsAsAFailedAccess()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);

        // Act
        var redeemed = await SetupCodes.TryRedeemAsync(graph.UserManager, user, "0000000000", graph.Time);

        // Assert
        Assert.False(redeemed);
        var stored = (await graph.UserManager.FindByIdAsync(user.Id))!;
        Assert.Equal(1, stored.AccessFailedCount);
        Assert.True(await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time));
    }

    [Fact]
    public async Task WhenAnExpiredCodeIsRedeemed_ThenItCountsAsAFailedAccess()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);
        graph.Time.Advance(SetupCodes.Lifetime + TimeSpan.FromSeconds(1));

        // Act
        var redeemed = await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time);

        // Assert
        Assert.False(redeemed);
        Assert.Equal(1, (await graph.UserManager.FindByIdAsync(user.Id))!.AccessFailedCount);
    }

    [Fact]
    public async Task WhenWrongCodesAreRedeemedRepeatedly_ThenTheUserIsLockedOutAndTheRightCodeIsRefused()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);
        var maximumAttempts = graph.UserManager.Options.Lockout.MaxFailedAccessAttempts;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            Assert.False(await SetupCodes.TryRedeemAsync(graph.UserManager, user, "0000000000", graph.Time));
        }

        // Act
        var redeemed = await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time);

        // Assert
        Assert.False(redeemed);
        var stored = (await graph.UserManager.FindByIdAsync(user.Id))!;
        Assert.True(stored.LockoutEnd > graph.Time.GetUtcNow());
        Assert.NotNull(await graph.UserManager.GetAuthenticationTokenAsync(stored, "HomeBlaze", "SetupCode"));
    }

    [Fact]
    public async Task WhenTheUserIsLockedOut_ThenEvenTheRightCodeFails()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var code = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);
        await graph.UserManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        // Act
        var redeemed = await SetupCodes.TryRedeemAsync(graph.UserManager, user, code, graph.Time);

        // Assert
        Assert.False(redeemed);
    }

    [Fact]
    public async Task WhenANewCodeIsCreated_ThenTheEarlierCodeFailsAndAPasswordChangeIsRequired()
    {
        // Arrange
        await using var graph = await AuthenticationTestGraph.CreateAsync();
        var user = await graph.CreateUserAsync("mom");
        var earlier = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);

        // Act
        var later = await SetupCodes.CreateAsync(graph.UserManager, user, graph.Time);

        // Assert
        Assert.True(user.MustChangePassword);
        Assert.False(await SetupCodes.TryRedeemAsync(graph.UserManager, user, earlier, graph.Time));
        Assert.True(await SetupCodes.TryRedeemAsync(graph.UserManager, user, later, graph.Time));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD1234")]
    public void WhenACodeIsCheckedWithoutAUser_ThenItIsRefused(string code)
    {
        // Act
        var isRedeemed = SetupCodes.RefuseWithoutUser(code);

        // Assert
        Assert.False(isRedeemed);
    }
}
