using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Change;

namespace Connectors.Server.Grinder;

/// <summary>
/// Source for a grinder device that owns the grind size of the bean hopper.
/// </summary>
#region GrinderSourceClass
public sealed class GrinderSource : SubjectSourceBase
{
    private readonly IInterceptorSubject _hopper;
    private readonly IGrinderDevice _device;
    private readonly SourceOwnershipManager _ownership;
    private readonly ILogger _logger;

    public override IInterceptorSubject RootSubject => _hopper;

    private PropertyReference GrindSize =>
        new(_hopper, nameof(BeanHopper.GrindSize));
    #endregion

    public GrinderSource(IInterceptorSubject hopper, IGrinderDevice device, ILogger<GrinderSource> logger)
        : base(hopper.Context, logger)
    {
        _hopper = hopper;
        _device = device;
        _logger = logger;
        _ownership = new SourceOwnershipManager(this);
    }

    #region StartListening
    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        // Claim before connecting, so changes made while offline are queued for it.
        if (!_ownership.ClaimSource(GrindSize))
        {
            throw new InvalidOperationException("The grind size already has another source.");
        }

        var connection = await _device.ConnectAsync(cancellationToken);
        return BackgroundTaskLifetime.Start(cancellationToken, _logger, async token =>
        {
            await foreach (var size in _device.WatchGrindSizeAsync(token))
            {
                propertyWriter.Write(size, ApplyGrindSize);
            }
        }, () => connection.DisposeAsync());
    }
    #endregion

    #region LoadInitialState
    public override async Task<Action?> LoadInitialStateAsync(
        CancellationToken cancellationToken)
    {
        var size = await _device.ReadGrindSizeAsync(cancellationToken);
        return () => ApplyGrindSize(size);
    }

    private void ApplyGrindSize(int size) =>
        GrindSize.SetValueFromSource(this, null, DateTimeOffset.UtcNow, size);
    #endregion

    #region WriteChanges
    public override async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var change in changes.ToArray())
            {
                var size = change.GetNewValue<int>();
                await _device.WriteGrindSizeAsync(size, cancellationToken);
            }
            return WriteResult.Success;
        }
        catch (Exception exception)
        {
            return WriteResult.Failure(changes, exception);
        }
    }
    #endregion

    public override void Dispose()
    {
        _ownership.Dispose();
        base.Dispose();
    }
}
