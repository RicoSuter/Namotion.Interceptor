using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Namotion.Interceptor.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Tests.Formal;

/// <summary>
/// Folds ModelTrace events into full-state snapshots (one per Commit) and appends one
/// newline-delimited JSON behavior to a shared trace file on dispose. On flush it
/// normalizes cover to the full item set seen (unset items default to "Retrying") and
/// prepends the model's Init snapshot, so a naturally-emitted incremental trace still
/// matches the model, which starts with every item present and pending. Consecutive
/// identical snapshots (no-op commits) are dropped, since the model has no stutter step.
/// </summary>
internal sealed class ModelTraceSink : IModelTraceSink, IDisposable
{
    private static readonly object FileGate = new();

    private readonly string _file;
    private readonly IModelTraceSink? _previous;
    private readonly object _gate = new();
    private readonly List<(string field, string? key, string value)> _events = new();
    private readonly List<int> _commits = new(); // event count at each commit boundary

    public ModelTraceSink(string file)
    {
        _file = file;
        _previous = ModelTrace.Sink.Value;
        ModelTrace.Sink.Value = this;
    }

    public void Record(string field, string? key, string value)
    {
        lock (_gate) _events.Add((field, key, value));
    }

    public void Commit()
    {
        lock (_gate) _commits.Add(_events.Count);
    }

    public void Dispose()
    {
        ModelTrace.Sink.Value = _previous;
        string? line;
        lock (_gate) line = BuildBehavior();
        if (line is null) return;
        lock (FileGate) File.AppendAllText(_file, line + "\n");
    }

    private string? BuildBehavior()
    {
        var items = _events.Where(e => e.field == "cover" && e.key is not null)
                           .Select(e => e.key!).Distinct().ToList();

        var scalar = new Dictionary<string, string>
        {
            ["state"] = "Disconnected", ["linkUp"] = "true",
            ["buffering"] = "false", ["stalled"] = "false",
        };
        var cover = items.ToDictionary(i => i, _ => "Retrying");

        Dictionary<string, object> Current() => new()
        {
            ["state"] = scalar["state"],
            ["linkUp"] = scalar["linkUp"] == "true",
            ["buffering"] = scalar["buffering"] == "true",
            ["stalled"] = scalar["stalled"] == "true",
            ["cover"] = new Dictionary<string, string>(cover),
        };

        var states = new List<Dictionary<string, object>> { Current() }; // seq 0 = Init
        var ev = 0;
        foreach (var boundary in _commits)
        {
            for (; ev < boundary; ev++)
            {
                var (field, key, value) = _events[ev];
                if (field == "cover" && key is not null) cover[key] = value;
                else scalar[field] = value;
            }
            var next = Current();
            if (!JsonEqual(states[^1], next)) states.Add(next);
        }

        if (states.Count < 2) return null;

        for (var i = 0; i < states.Count; i++) states[i]["seq"] = i;
        return JsonSerializer.Serialize(states);
    }

    private static bool JsonEqual(object a, object b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}
