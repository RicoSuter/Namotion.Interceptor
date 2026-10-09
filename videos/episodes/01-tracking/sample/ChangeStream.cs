using System.Collections;
using System.Globalization;
using System.Reactive.Linq;
using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

/// <summary>
/// The latest changes of one machine for the change stream page: counters for a few properties and the newest
/// entries without the simulator's physics. Fed by two observable subscriptions and the lifecycle events, so every
/// member is safe to call from any thread.
/// </summary>
public sealed class ChangeStream : IDisposable
{
    private const int MaximumEntries = 8;

    /// <summary>Properties the simulator writes on every tick; counted, but kept out of the list.</summary>
    private static readonly HashSet<string> Physics = ["Temperature", "Pressure", "HeaterOn", "Level"];

    private readonly Lock _lock = new();
    private readonly LinkedList<StreamEntry> _entries = new();
    private readonly IDisposable _counter;
    private readonly IDisposable _log;
    private long _sequence;
    private int _temperatureChanges;
    private int _statusChanges;
    private int _readyChanges;
    private DateTimeOffset _clearedAt = DateTimeOffset.MinValue;

    public ChangeStream(IInterceptorSubjectContext context)
    {
        #region Observable
        var changes = context.GetPropertyChangeObservable();

        _counter = changes.Subscribe(Count);

        _log = changes
            .Where(change => !Physics.Contains(change.Property.Name))
            .Subscribe(change => Add(
                change.Property.Name,
                change.GetOldValue<object?>(),
                change.GetNewValue<object?>(),
                change.ChangedTimestamp));
        #endregion
    }

    /// <summary>Adds a lifecycle event, such as a recipe that joined the graph.</summary>
    public void AddLifecycle(string kind, IInterceptorSubject subject)
    {
        var name = subject is Recipe recipe ? $"Recipe {recipe.Name}" : subject.GetType().Name;
        lock (_lock)
        {
            Append(new StreamEntry(++_sequence, kind, "Lifecycle", null, name));
        }
    }

    /// <summary>Forgets every entry and count; changes written before this call are ignored when they arrive late.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _temperatureChanges = 0;
            _statusChanges = 0;
            _readyChanges = 0;
            _clearedAt = DateTimeOffset.UtcNow;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                Counts = new { Temperature = _temperatureChanges, Status = _statusChanges, IsReady = _readyChanges },
                Entries = _entries.ToArray()
            };
        }
    }

    public void Dispose()
    {
        _counter.Dispose();
        _log.Dispose();
    }

    private void Count(SubjectPropertyChange change)
    {
        lock (_lock)
        {
            if (change.ChangedTimestamp <= _clearedAt)
            {
                return;
            }

            switch (change.Property.Name)
            {
                case nameof(Boiler.Temperature) when change.Property.Subject is Boiler:
                    _temperatureChanges++;
                    break;
                case nameof(CoffeeMachine.Status):
                    _statusChanges++;
                    break;
                case nameof(CoffeeMachine.IsReady):
                    _readyChanges++;
                    break;
            }
        }
    }

    private void Add(string property, object? oldValue, object? newValue, DateTimeOffset changedTimestamp)
    {
        lock (_lock)
        {
            if (changedTimestamp > _clearedAt)
            {
                Append(new StreamEntry(++_sequence, "change", property, Format(oldValue), Format(newValue)));
            }
        }
    }

    private void Append(StreamEntry entry)
    {
        _entries.AddFirst(entry);
        if (_entries.Count > MaximumEntries)
        {
            _entries.RemoveLast();
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        string text => $"\"{text}\"",
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        ICollection collection => $"{collection.Count} recipes",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public sealed record StreamEntry(long Sequence, string Kind, string Property, string? OldValue, string? NewValue);
}
