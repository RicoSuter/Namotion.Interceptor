using System.Reactive.Concurrency;
using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;

/// <summary>
/// One coffee machine in its own context, with its change stream, its lifecycle rows and a log of when it is ready.
/// </summary>
public sealed class MachineHost : IDisposable
{
    private readonly LifecycleInterceptor _lifecycle;
    private readonly IDisposable _readyLog;

    public MachineHost(string id, bool brewsInTransaction, ILogger logger)
    {
        Id = id;
        BrewsInTransaction = brewsInTransaction;

        #region ContextWithTransactions
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithDataAnnotationValidation()
            .WithTransactions();

        var machine = new CoffeeMachine(context);
        #endregion

        Machine = machine;
        Stream = new ChangeStream(context);

        #region Lifecycle
        _lifecycle = context.TryGetLifecycleInterceptor()!;
        _lifecycle.SubjectAttached += OnAttached;
        _lifecycle.SubjectDetaching += OnDetaching;
        #endregion

        #region ReadyLog
        _readyLog = machine.SubscribeToProperty(
            x => x.IsReady,
            (in SubjectPropertyChange change) => logger.LogInformation(
                "{Machine} is ready: {IsReady}", id, change.GetNewValue<bool>()),
            Scheduler.Default,
            onError: exception => logger.LogError(exception, "Logging {Machine} failed", id));
        #endregion
    }

    public string Id { get; }

    /// <summary>Whether brews run <see cref="CoffeeMachine.BrewAsync"/> instead of <see cref="CoffeeMachine.Brew"/>.</summary>
    public bool BrewsInTransaction { get; }

    public CoffeeMachine Machine { get; }

    public ChangeStream Stream { get; }

    public async Task BrewAsync(string recipeName)
    {
        if (BrewsInTransaction)
        {
            await Machine.BrewAsync(recipeName);
        }
        else
        {
            Machine.Brew(recipeName);
        }
    }

    public void Dispose()
    {
        _lifecycle.SubjectAttached -= OnAttached;
        _lifecycle.SubjectDetaching -= OnDetaching;
        _readyLog.Dispose();
        Stream.Dispose();
    }

    // Lifecycle events run inside the lifecycle lock: only a quick in-memory append here.
    private void OnAttached(SubjectLifecycleChange change) => Stream.AddLifecycle("attached", change.Subject);

    private void OnDetaching(SubjectLifecycleChange change) => Stream.AddLifecycle("detaching", change.Subject);
}

/// <summary>The two machines of the sample: one brews with <c>Brew</c>, the other with <c>BrewAsync</c>.</summary>
public sealed class MachineHosts : IDisposable
{
    public static readonly string[] Ids = ["brew", "brew-async"];

    private readonly Dictionary<string, MachineHost> _hosts;

    public MachineHosts(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tracking.Sample");
        _hosts = Ids.ToDictionary(id => id, id => new MachineHost(id, brewsInTransaction: id == "brew-async", logger));
    }

    public bool Contains(string id) => _hosts.ContainsKey(id);

    public MachineHost Get(string id) => _hosts[id];

    public void Dispose()
    {
        foreach (var host in _hosts.Values)
        {
            host.Dispose();
        }
    }
}
