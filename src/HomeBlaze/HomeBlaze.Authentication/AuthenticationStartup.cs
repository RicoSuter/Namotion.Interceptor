using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Authentication;

/// <summary>
/// Once the root is loaded, initializes the authentication database, seeds it, loads the roles and users, and keeps
/// them shown in the single <c>Authorization</c> subject, adding one to the root storage when none exists, then marks the
/// database ready (<see cref="AuthenticationDatabase.MarkReady"/>). Runs in the background, so that the web host starts
/// even while the root does not load; until then every page shows "authentication unavailable". A failure is logged and
/// leaves the database unavailable, without stopping the host.
/// </summary>
/// <remarks>
/// A failure to attach the projection or to add the <c>Authorization</c> subject is logged by the
/// <see cref="AuthorizationSubjectBinding"/>, and the startup still becomes ready without a projection. That fails closed
/// for administration: the role and user dialogs and every change of users need the subject, so they refuse.
/// </remarks>
public sealed class AuthenticationStartup : BackgroundService
{
    /// <summary>Why authentication is unavailable when the roles and users cannot be shown in the graph.</summary>
    public const string BindingFailedReason = "The roles and users could not be shown in the Authorization subject of the root storage.";

    private readonly RootManager _rootManager;
    private readonly AuthenticationDatabase _database;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RoleStore _roleStore;
    private readonly UserStore _userStore;
    private readonly AuthorizationSubjectBinding _binding;
    private readonly ILogger _logger;

    internal AuthenticationStartup(
        RootManager rootManager,
        AuthenticationDatabase database,
        IServiceScopeFactory scopeFactory,
        RoleStore roleStore,
        UserStore userStore,
        AuthorizationSubjectBinding binding,
        ILogger<AuthenticationStartup> logger)
    {
        _rootManager = rootManager;
        _database = database;
        _scopeFactory = scopeFactory;
        _roleStore = roleStore;
        _userStore = userStore;
        _binding = binding;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IInterceptorSubject root;
        try
        {
            root = await _rootManager.RootLoaded.WaitAsync(stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "The root did not load, so authentication is unavailable.");
            _database.MarkUnavailable("The root did not load.");
            return;
        }

        await LoadAsync(stoppingToken);
        try
        {
            await _binding.StartAsync(root, stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "The roles and users could not be shown in the Authorization subject, so authentication is unavailable.");
            if (_database.IsAvailable)
            {
                _database.MarkUnavailable(BindingFailedReason);
            }

            return;
        }

        _database.MarkReady();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.InitializeAsync(cancellationToken);
            var outcome = await AdminSeeding.SeedAsync(_scopeFactory, _database, cancellationToken);
            switch (outcome)
            {
                case SeedOutcome.Seeded:
                    _logger.LogWarning("Seeded the administrator {UserName} with an empty password; sign in with it and set a password.", AdminSeeding.AdminUserName);
                    break;
                case SeedOutcome.MarkerWithoutUsers:
                    _logger.LogError("Authentication is unavailable: {Reason}", _database.UnavailableReason);
                    break;
            }

            if (_database.IsAvailable)
            {
                await _roleStore.LoadAsync(cancellationToken);
                await _userStore.LoadAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "The authentication database could not be loaded, so authentication is unavailable.");
            // A reason recorded already, such as the failure of seeding, is the more precise one.
            if (_database.IsAvailable)
            {
                _database.MarkUnavailable("The roles and users could not be loaded from the authentication database.");
            }
        }
    }
}
