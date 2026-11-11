using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class RevalidationTests
{
    private const string UserName = "kid";

    [Fact]
    public async Task WhenTheUserIsRemoved_ThenTheCircuitBecomesUnauthenticatedWithoutWaitingForTheInterval()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        circuit.Provider.AuthenticationStateChanged += _ => changed.TrySetResult();

        // Act
        var removed = await host.AdministrationAsAdmin.RemoveAsync(UserName, CancellationToken.None);

        // Assert
        Assert.True(removed.Succeeded, removed.Error);
        await changed.Task.WaitAsync(TestTimeouts.Default);
        Assert.False((await circuit.Provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task WhenThePasswordOfTheUserIsReset_ThenTheCircuitBecomesUnauthenticated()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        circuit.Provider.AuthenticationStateChanged += _ => changed.TrySetResult();

        // Act
        var result = await host.AdministrationAsAdmin.ResetPasswordAsync(UserName, CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded, result.Error);
        await changed.Task.WaitAsync(TestTimeouts.Default);
        Assert.False((await circuit.Provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task WhenTheStoreLearnsOfAnAdministratorLockWithoutANewStamp_ThenTheCircuitBecomesUnauthenticated()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        await circuit.Provider.LastRevalidation;
        await host.LockOutAsync(UserName, ApplicationUser.AdministratorLockoutEnd);

        // Act
        await host.UserStore.RefreshUserAsync(host.UserStore.GetUserId(UserName)!, CancellationToken.None);
        await circuit.Provider.LastRevalidation;

        // Assert
        Assert.True(await circuit.Provider.ValidateAsync(circuit.Principal, CancellationToken.None));
        Assert.False((await circuit.Provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task WhenTheRolesOfTheUserChange_ThenTheCircuitStaysSignedInAndTheNewPolicyApplies()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        var permissions = circuit.Services.GetRequiredService<IUserPermissions>();
        var roles = host.AuthorizationSubjects.Single().TryGetRegisteredSubject()!.TryGetProperty("Roles")!;
        Assert.False(permissions.CanAdminister(roles));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        permissions.Changed += () => changed.TrySetResult();

        // Act
        var result = await host.AdministrationAsAdmin.SetRolesAsync(UserName, [BuiltInRoles.EverythingName], CancellationToken.None);
        await circuit.Provider.LastRevalidation;

        // Assert
        Assert.True(result.Succeeded, result.Error);
        await changed.Task.WaitAsync(TestTimeouts.Default);
        Assert.Same(circuit.Principal, (await circuit.Provider.GetAuthenticationStateAsync()).User);
        Assert.True(permissions.CanAdminister(roles));
    }

    [Fact]
    public async Task WhenAnotherUserChanges_ThenOnlyTheCircuitsOfThatUserAreRevalidated()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        var added = await host.AdministrationAsAdmin.AddUserAsync("other", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        // A stamp changed without a change of the store, which only a revalidation of this user notices.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await userManager.UpdateSecurityStampAsync((await userManager.FindByNameAsync(UserName))!)).Succeeded);
        }

        // Act
        var result = await host.AdministrationAsAdmin.SetRolesAsync("other", [BuiltInRoles.OperatorName], CancellationToken.None);
        await circuit.Provider.LastRevalidation;
        var principalAfterOtherChange = (await circuit.Provider.GetAuthenticationStateAsync()).User;
        await host.UserStore.RefreshUserAsync(host.UserStore.GetUserId(UserName)!, CancellationToken.None);
        await circuit.Provider.LastRevalidation;

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Same(circuit.Principal, principalAfterOtherChange);
        Assert.False((await circuit.Provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task WhenAReconnectSetsAPrincipalWhoseSessionEnded_ThenTheCircuitStaysSignedOutAndNoSubscriberSeesThePrincipal()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        await circuit.Provider.LastRevalidation;
        await host.ChangeSecurityStampAsync(UserName);
        await host.UserStore.RefreshUserAsync(host.UserStore.GetUserId(UserName)!, CancellationToken.None);
        await circuit.Provider.LastRevalidation;
        Assert.False((await circuit.Provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        var currentUser = circuit.Services.GetRequiredService<CurrentUser>();
        var delivered = new List<ClaimsPrincipal>();
        circuit.Provider.AuthenticationStateChanged += state => delivered.Add(state.Result.User);

        // Act
        ((IHostEnvironmentAuthenticationStateProvider)circuit.Provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(circuit.Principal)));
        await circuit.Provider.LastRevalidation;

        // Assert
        Assert.False((await circuit.Provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.All(delivered, principal => Assert.False(principal.Identity?.IsAuthenticated));
        Assert.Null(currentUser.UserId);
    }

    [Fact]
    public async Task WhenTheFirstStateIsStale_ThenEverySubscriberEndsOnTheAnonymousState()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        ClaimsPrincipal stalePrincipal;
        await using (var signedIn = await SignedInCircuit.StartAsync(host, UserName))
        {
            stalePrincipal = signedIn.Principal;
        }

        await host.ChangeSecurityStampAsync(UserName);
        await host.UserStore.RefreshUserAsync(host.UserStore.GetUserId(UserName)!, CancellationToken.None);
        await using var scope = host.Services.CreateAsyncScope();
        var provider = (HomeBlazeRevalidatingAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var currentUser = scope.ServiceProvider.GetRequiredService<CurrentUser>();
        var delivered = new List<ClaimsPrincipal>();
        provider.AuthenticationStateChanged += state => delivered.Add(state.Result.User);

        // Act
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(stalePrincipal)));
        await provider.LastRevalidation;

        // Assert
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.NotEmpty(delivered);
        Assert.All(delivered, principal => Assert.False(principal.Identity?.IsAuthenticated));
        Assert.Null(currentUser.UserId);
    }

    [Fact]
    public async Task WhenACircuitStartsWithAStaleSecurityStamp_ThenItIsSignedOutWithoutWaitingForTheInterval()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        var stalePrincipal = circuit.Principal;
        var reset = await host.AdministrationAsAdmin.ResetPasswordAsync(UserName, CancellationToken.None);
        Assert.True(reset.Succeeded, reset.Error);
        await using var newCircuitScope = host.Services.CreateAsyncScope();
        var provider = (HomeBlazeRevalidatingAuthenticationStateProvider)newCircuitScope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();

        // Act
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(stalePrincipal)));
        await provider.LastRevalidation;

        // Assert
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task WhenACircuitStartsWithACurrentSecurityStamp_ThenItStaysSignedIn()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);

        // Act
        await circuit.Provider.LastRevalidation;

        // Assert
        Assert.Same(circuit.Principal, (await circuit.Provider.GetAuthenticationStateAsync()).User);
    }

    [Fact]
    public async Task WhenTheSecurityStampIsUnchanged_ThenTheStateIsValid()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);

        // Act
        var isValid = await circuit.Provider.ValidateAsync(circuit.Principal, CancellationToken.None);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public async Task WhenThePrincipalHasNoSecurityStamp_ThenTheStateIsInvalid()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var circuit = await SignedInCircuit.StartAsync(host, UserName);
        var userId = host.UserStore.GetUserId(UserName)!;
        var withoutStamp = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], IdentityConstants.ApplicationScheme));

        // Act
        var isValid = await circuit.Provider.ValidateAsync(withoutStamp, CancellationToken.None);

        // Assert
        Assert.False(isValid);
    }

    /// <summary>The authentication state provider of a circuit scope in which <c>kid</c>, a Reader, is signed in.</summary>
    private sealed class SignedInCircuit : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;

        private SignedInCircuit(AsyncServiceScope scope, HomeBlazeRevalidatingAuthenticationStateProvider provider, ClaimsPrincipal principal)
        {
            _scope = scope;
            Provider = provider;
            Principal = principal;
        }

        public HomeBlazeRevalidatingAuthenticationStateProvider Provider { get; }

        public ClaimsPrincipal Principal { get; }

        /// <summary>The services of the circuit scope.</summary>
        public IServiceProvider Services => _scope.ServiceProvider;

        public static async Task<SignedInCircuit> StartAsync(AuthenticationTestHost host, string userName)
        {
            var added = await host.AdministrationAsAdmin.AddUserAsync(userName, null, [BuiltInRoles.ReaderName], CancellationToken.None);
            Assert.True(added.Succeeded, added.Error);

            var scope = host.Services.CreateAsyncScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByNameAsync(userName))!;
            // As after setting a password, without which the person may do nothing.
            user.MustChangePassword = false;
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
            await host.UserStore.RefreshUserAsync(user.Id, CancellationToken.None);
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
            var provider = (HomeBlazeRevalidatingAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
            ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
            return new SignedInCircuit(scope, provider, principal);
        }

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }
}
