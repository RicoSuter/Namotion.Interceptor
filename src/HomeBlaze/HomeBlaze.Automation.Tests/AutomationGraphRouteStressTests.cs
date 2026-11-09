using System.Runtime.CompilerServices;
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit.Abstractions;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

/// <summary>
/// Writers change the structure of the graph concurrently while automations are stopped, started, saved and reset, and
/// then in short rounds while the automations publish their routes late. After each round a graph change that every
/// automation is given anyway, as without routes, must change nothing: an automation that missed a structural change
/// that moves one of its paths would resolve it then. A round where an automation still waits for the next graph change,
/// for triggers that a refresh outside a graph change resolved differently, is left out, since that graph change changes
/// it as without routes.
/// </summary>
[Trait("Category", "Integration")]
public class AutomationGraphRouteStressTests(ITestOutputHelper output)
{
    private const int AutomationCount = 4;
    private const int WriterCount = 4;
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(2);

    public static TheoryData<int, bool> Seeds
    {
        get
        {
            var seeds = new TheoryData<int, bool>();
            for (var seed = 1; seed <= 8; seed++)
            {
                seeds.Add(seed, false);
            }

            seeds.Add(1000, true);
            return seeds;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task WhenWritersChangeTheStructureConcurrently_ThenAGraphChangeAfterwardsChangesNoAutomation(int seed, bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var world = await StressWorld.CreateAsync(seed, makesEveryRouteUnbounded);

        // Act
        using var stop = new CancellationTokenSource();
        var control = world.ControlAsync(seed, stop.Token);
        await Task.WhenAll(world.StartWriters(phase: 0, iterations: 2000)).WaitAsync(Bound);
        await stop.CancelAsync();
        await control.WaitAsync(Bound);
        await world.StartStoppedAsync();
        await world.FlushAsync();
        world.DelayPublications();
        var skipped = 0;
        for (var round = 0; round < 100; round++)
        {
            await Task.WhenAll(world.StartWriters(round + 1, iterations: 10)).WaitAsync(Bound);
            await world.FlushAsync();
            var waits = world.WaitsForGraphChange();
            var before = world.Observe();
            await world.ForceGraphChangeAsync();
            var after = world.Observe();
            if (waits)
            {
                skipped++;
                continue;
            }

            // Assert
            Assert.True(before == after, $"Seed {seed}, round {round}:\n{Difference(before, after)}");
        }

        output.WriteLine($"Seed {seed}: {skipped} of 100 rounds waited for a graph change.");
        world.AssertResolvedFreshly(makesEveryRouteUnbounded);
    }

    private static string Difference(string before, string after)
    {
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');
        var text = new StringBuilder();
        for (var index = 0; index < Math.Max(beforeLines.Length, afterLines.Length); index++)
        {
            var beforeLine = beforeLines.ElementAtOrDefault(index);
            var afterLine = afterLines.ElementAtOrDefault(index);
            if (beforeLine != afterLine)
            {
                text.Append("before: ").AppendLine(beforeLine).Append("after:  ").AppendLine(afterLine);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Rooms at <c>/R0</c> to <c>/R3</c>, folders <c>/F0</c>, <c>/F0/F2</c> and <c>/F1</c>, an unrelated holder, a rule
    /// host and four automations, one at the root and one in each folder at first.
    /// </summary>
    private sealed class StressWorld : IAsyncDisposable
    {
        private readonly AutomationTestHost _host;
        private readonly Lock _structureLock = new();
        private readonly Lock _windLock = new();
        private readonly TestRoom[] _rooms = new TestRoom[4];
        private readonly TestFolder[] _folders = [new() { Level = 3 }, new() { Level = 7 }, new() { Level = 9 }];
        private readonly TestSensorHolder _holder = new();
        private readonly TestRuleHost _ruleHost = new();
        private readonly Automation[] _automations = new Automation[AutomationCount];
        private readonly List<int>[] _containers = new List<int>[AutomationCount];
        private readonly bool[] _running = [.. Enumerable.Repeat(true, AutomationCount)];
        private int _seed;

        private StressWorld(AutomationTestHost host)
        {
            _host = host;
        }

        private TestGraph Graph => _host.Graph;

        public static async Task<StressWorld> CreateAsync(int seed, bool makesEveryRouteUnbounded)
        {
            var host = await AutomationTestHost.StartAsync(_ => { });
            host.ChangeDispatcher.MakesEveryRouteUnbounded = makesEveryRouteUnbounded;
            var world = new StressWorld(host) { _seed = seed };
            await world.BuildAsync(new Random(seed));
            return world;
        }

        public Task[] StartWriters(int phase, int iterations) => [.. Enumerable.Range(0, WriterCount).Select(writer => Task.Run(() =>
        {
            var random = new Random(_seed * 100 + writer + phase * 17);
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                Write(random);
            }
        }))];

        public Task ControlAsync(int seed, CancellationToken cancellationToken) => Task.Run(async () =>
        {
            var random = new Random(seed * 7919);
            while (!cancellationToken.IsCancellationRequested)
            {
                await ControlOneAsync(random);
            }
        }, CancellationToken.None);

        public async Task StartStoppedAsync()
        {
            for (var index = 0; index < AutomationCount; index++)
            {
                if (!_running[index])
                {
                    await ((IHostedService)_automations[index]).StartAsync(CancellationToken.None).WaitAsync(Bound);
                    _running[index] = true;
                }
            }
        }

        public async Task FlushAsync()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var automation in _automations)
                {
                    await automation.FlushAsync().WaitAsync(Bound);
                }
            }
        }

        // A late publication widens the time in which a structural change commits while the worker resolves.
        public void DelayPublications()
        {
            foreach (var automation in _automations)
            {
                automation.Runtime!.Routes.BeforePublication = () => Thread.SpinWait(Random.Shared.Next(50000));
            }
        }

        public bool WaitsForGraphChange() => _automations.Any(automation => automation.Runtime!.Routes.WaitsForGraphChange);

        public async Task ForceGraphChangeAsync()
        {
            foreach (var automation in _automations)
            {
                ((IAutomationChangeListener)automation.Runtime!.Changes).OnGraphChanged(0);
            }

            await FlushAsync();
        }

        public string Observe()
        {
            var text = new StringBuilder();
            foreach (var automation in _automations)
            {
                var runtime = automation.Runtime!;
                text.Append(automation.Name).Append(" at ").Append(runtime.Location.AutomationPath).Append(", state ").Append(automation.CurrentState).Append('\n');
                text.Append("  union [").AppendJoin(", ", runtime.Routes.Union.Select(Format).Order(StringComparer.Ordinal)).Append("]\n");
                foreach (var rule in runtime.Engine.Rules.OrderBy(rule => rule.Key, StringComparer.Ordinal))
                {
                    text.Append("  ").Append(rule.Key).Append(" runs ").Append(rule.Rule.RunCount)
                        .Append(" only if [").AppendJoin(", ", rule.OnlyIfDependencies.Select(Format)).Append("]\n");
                    foreach (var trigger in rule.PropertyTriggers)
                    {
                        text.Append("    p").Append(trigger.Index).Append(" [").AppendJoin(", ", trigger.Dependencies.Select(Format)).Append("]\n");
                    }

                    foreach (var trigger in rule.EventTriggers)
                    {
                        text.Append("    e ").Append(trigger.Source is null ? "null" : FormatSubject(trigger.Source)).Append(' ').Append(trigger.SourceError).Append('\n');
                    }
                }
            }

            return text.ToString();
        }

        public void AssertResolvedFreshly(bool makesEveryRouteUnbounded)
        {
            foreach (var automation in _automations)
            {
                var runtime = automation.Runtime!;
                if (Graph.Resolver.GetPath(automation, PathStyle.Canonical) is { } expectedPath)
                {
                    Assert.Equal(expectedPath, runtime.Location.AutomationPath);
                }

                var registration = runtime.Changes.Registration!;
                if (!makesEveryRouteUnbounded && !registration.IsUnbounded)
                {
                    Assert.True(registration.Route.SetEquals(runtime.Routes.Union), $"{automation.Name}: the published route differs from the union");
                }

                foreach (var rule in runtime.Engine.Rules)
                {
                    foreach (var trigger in rule.PropertyTriggers.Where(trigger => trigger.State.Phase != TriggerPhase.Inactive && rule.IsActivated))
                    {
                        var fresh = trigger.PropertyPaths
                            .Select(path => AutomationPaths.ResolveProperty(Graph.Resolver, path, rule.Rule))
                            .OfType<RegisteredSubjectProperty>()
                            .Select(property => property.Reference);
                        Assert.True(fresh.SequenceEqual(trigger.Dependencies), $"{automation.Name}/{rule.Key}: the dependencies are out of date");
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _host.Blinds.Release();
            await _host.DisposeAsync();
        }

        private async Task BuildAsync(Random random)
        {
            for (var index = 0; index < _rooms.Length; index++)
            {
                _rooms[index] = CreateRoom(random);
                Graph.Add("R" + index, _rooms[index]);
            }

            Graph.Add("F0", _folders[0]);
            Graph.Add("F1", _folders[1]);
            _folders[0].Add("F2", _folders[2]);
            _folders[1].Add("Button", new TestButton());
            Graph.Add("Holder", _holder);
            Graph.Add("RuleHost", _ruleHost);
            for (var index = 0; index < AutomationCount; index++)
            {
                var container = index % 4 - 1;
                var rules = CreateRules(index);
                _containers[index] = [container];
                _automations[index] = await _host.AddAutomationAsync(
                    "A" + index,
                    automation =>
                    {
                        automation.States = ["Day", "Night"];
                        automation.InitialState = "Day";
                        automation.Rules = rules;
                    },
                    automation => AddTo(container, automation));
            }
        }

        private void Write(Random random)
        {
            var room = _rooms[random.Next(_rooms.Length)];
            switch (random.Next(14))
            {
                case 0:
                    ChangeDevices(random, room);
                    break;
                case 1:
                    ChangeItems(random, room);
                    break;
                case 2:
                    room.Sensor = random.Next(3) == 0 ? null : CreateSensor(random);
                    break;
                case 3:
                    room.Button = random.Next(2) == 0 ? null : new TestButton();
                    break;
                case 4:
                    ReplaceRoom(random);
                    break;
                case 5:
                    ChangeFolderChild(random);
                    break;
                case 6:
                    MoveAutomation(random);
                    break;
                case 7:
                    AddWind(random, room);
                    break;
                case 8:
                    MoveFolder();
                    break;
                case 9:
                    _holder.Sensor = _holder.Sensor is null ? CreateSensor(random) : null;
                    break;
                case 10:
                    ShareRule(random);
                    break;
                case 11:
                    _folders[random.Next(_folders.Length)].Level = random.Next(10);
                    break;
                default:
                    WriteValue(random, room);
                    break;
            }
        }

        private static void ChangeDevices(Random random, TestRoom room)
        {
            var devices = new Dictionary<string, TestSensor>(room.Devices);
            var key = "d" + random.Next(3);
            if (random.Next(3) == 0)
            {
                devices.Remove(key);
            }
            else
            {
                devices[key] = CreateSensor(random);
            }

            room.Devices = devices;
        }

        private static void ChangeItems(Random random, TestRoom room)
        {
            var items = new List<TestSensor>(room.Items);
            if (random.Next(2) == 0 && items.Count > 0)
            {
                items.RemoveAt(random.Next(items.Count));
            }
            else
            {
                items.Insert(random.Next(items.Count + 1), CreateSensor(random));
            }

            room.Items = items;
        }

        private void ReplaceRoom(Random random)
        {
            lock (_structureLock)
            {
                var index = random.Next(_rooms.Length);
                if (random.Next(4) == 0)
                {
                    Graph.Remove("R" + index);
                    return;
                }

                _rooms[index] = CreateRoom(random);
                Graph.Add("R" + index, _rooms[index]);
            }
        }

        private void ChangeFolderChild(Random random)
        {
            lock (_structureLock)
            {
                var folder = _folders[random.Next(_folders.Length)];
                if (random.Next(2) == 0)
                {
                    folder.Remove("S0");
                }
                else
                {
                    folder.Add("S0", CreateSensor(random));
                }
            }
        }

        private void MoveAutomation(Random random)
        {
            lock (_structureLock)
            {
                var index = random.Next(AutomationCount);
                var containers = _containers[index];
                var target = random.Next(-1, _folders.Length);
                if (!containers.Contains(target))
                {
                    AddTo(target, _automations[index]);
                    containers.Add(target);
                }

                if (containers.Count > 1 && random.Next(4) != 0)
                {
                    var source = containers[random.Next(containers.Count - 1)];
                    RemoveFrom(source, _automations[index]);
                    containers.Remove(source);
                }
            }
        }

        private void AddWind(Random random, TestRoom room)
        {
            if (room.Sensor is not { } sensor || ((IInterceptorSubject)sensor).TryGetRegisteredSubject() is not { } registered)
            {
                return;
            }

            lock (_windLock)
            {
                if (registered.TryGetProperty("Wind") is null)
                {
                    var wind = random.Next(2) == 0 ? null : (double?)random.Next(10);
                    registered.AddProperty("Wind", typeof(double?), _ => wind, (_, value) => wind = (double?)value);
                }
            }
        }

        private void MoveFolder()
        {
            lock (_structureLock)
            {
                var inner = _folders[2];
                var (source, target) = _folders[0].Children.ContainsKey("F2") ? (_folders[0], _folders[1]) : (_folders[1], _folders[0]);
                target.Add("F2", inner);
                source.Remove("F2");
            }
        }

        private void ShareRule(Random random)
        {
            var shared = _automations[random.Next(AutomationCount)];
            _ruleHost.Rules = random.Next(2) == 0 || !shared.Rules.TryGetValue("level", out var level)
                ? []
                : new Dictionary<string, AutomationRule> { ["shared"] = level };
        }

        private static void WriteValue(Random random, TestRoom room)
        {
            var sensors = room.Devices.Values.Concat(room.Items).Append(room.Sensor).OfType<TestSensor>().ToList();
            if (sensors.Count > 0)
            {
                sensors[random.Next(sensors.Count)].Gust = random.Next(10);
            }
        }

        private async Task ControlOneAsync(Random random)
        {
            var index = random.Next(AutomationCount);
            var automation = _automations[index];
            switch (random.Next(4))
            {
                case 0:
                    await ToggleRunningAsync(index);
                    break;
                case 1:
                    automation.Rules = automation.Rules.ToDictionary(rule => rule.Key, rule => Copy(rule.Value));
                    await automation.ApplyConfigurationAsync(CancellationToken.None);
                    break;
                case 2:
                    if (_running[index])
                    {
                        automation.ResetState();
                    }

                    break;
                default:
                    await Task.Yield();
                    break;
            }
        }

        private async Task ToggleRunningAsync(int index)
        {
            var service = (IHostedService)_automations[index];
            if (_running[index])
            {
                await service.StopAsync(CancellationToken.None).WaitAsync(Bound);
            }
            else
            {
                await service.StartAsync(CancellationToken.None).WaitAsync(Bound);
            }

            _running[index] = !_running[index];
        }

        private static AutomationRule Copy(AutomationRule rule) => new()
        {
            Name = rule.Name,
            Mode = rule.Mode,
            InState = rule.InState,
            Triggers = [.. rule.Triggers],
            OnlyIf = rule.OnlyIf,
            Actions = [.. rule.Actions]
        };

        private string Format(PropertyReference property) => FormatSubject(property.Subject) + "." + property.Name;

        private string FormatSubject(IInterceptorSubject subject) =>
            (Graph.Resolver.GetPath(subject, PathStyle.Canonical) ?? "?") + "#" + RuntimeHelpers.GetHashCode(subject);

        private void AddTo(int container, Automation automation)
        {
            if (container < 0)
            {
                Graph.Add(automation.Name!, automation);
            }
            else
            {
                _folders[container].Add(automation.Name!, automation);
            }
        }

        private void RemoveFrom(int container, Automation automation)
        {
            if (container < 0)
            {
                Graph.Remove(automation.Name!);
            }
            else
            {
                _folders[container].Remove(automation.Name!);
            }
        }

        private static Dictionary<string, AutomationRule> CreateRules(int index)
        {
            var rules = Keyed(
                ("keyed", Create("Keyed", AutomationRuleMode.DryRun, [When("/R0/Devices[d0]/Gust > 5")], [Invoke("/Blinds/Close")])),
                ("mixed", On("Mixed", When("/R1/Items[1]/Rain > 5 and /R2/Sensor/Power > 5"), Invoke("/Blinds/Close"))),
                ("inline", On("Inline", When("/F0/S0/Gust > 5 or /F0/F2/S0/Rain > 5"), Invoke("/Blinds/Open"))),
                ("level", Create("Level", AutomationRuleMode.DryRun, [When("../../Level > 5")], [Invoke("/Blinds/Close")])),
                ("dynamic", On("Dynamic", When("/R0/Sensor/Wind > 5 or /R3/Sensor/Wind > 5"), Invoke("/Blinds/Close"))),
                ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/R1/Button", "TestPressEvent")], [Invoke("/Blinds/Open")])),
                ("sibling", Create("Sibling", AutomationRuleMode.DryRun, [OnEvent("../../Button", "TestPressEvent")], [Invoke("/Blinds/Open")])),
                ("entered", Create("Entered", AutomationRuleMode.DryRun, [OnEvent("..", "AutomationStateChangedEvent")], [Invoke("/Blinds/Open")])),
                ("guarded", Create("Guarded", AutomationRuleMode.On, [When("/R2/Devices[d1]/Gust > 5")], [Invoke("/Blinds/Close")], onlyIf: "/R0/Items[0]/Power > 5")),
                ("rearm", On("Rearm", When("/R1/Sensor/Gust > 5", rearm: "/R2/Sensor/Rain < 3"), Invoke("/Blinds/Close"))),
                ("self", Create("Self", AutomationRuleMode.DryRun, [When("./RunCount > 2 or ../CurrentState == Night")], [Invoke("/Blinds/Close")])),
                ("night", Create("Night", AutomationRuleMode.On, [When("/R0/Devices[d1]/Rain > 5")], [Invoke("/Blinds/Close")], inState: "Night")),
                ("toNight", Create("To night", AutomationRuleMode.On, [When("/R3/Items[0]/Gust > 7")], [Enter("Night")], inState: "Day")),
                ("toDay", Create("To day", AutomationRuleMode.On, [When("/R3/Items[0]/Gust < 3")], [Enter("Day")], inState: "Night")),
                ("deep", On("Deep", When($"/R{index % 4}/Devices[d2]/Gust > 5 or /R{(index + 1) % 4}/Items[2]/Rain > 5"), Invoke("/Blinds/Close"))));
            if (index == 2)
            {
                rules["above"] = Create("Above", AutomationRuleMode.DryRun, [When("../../../Level > 5")], [Invoke("/Blinds/Close")]);
            }

            return rules;
        }

        private static TestRoom CreateRoom(Random random) => new()
        {
            Level = random.Next(10),
            Devices = new[] { "d0", "d1", "d2" }.Where(_ => random.Next(2) == 0).ToDictionary(key => key, _ => CreateSensor(random)),
            Items = [.. Enumerable.Range(0, random.Next(4)).Select(_ => CreateSensor(random))],
            Sensor = random.Next(3) == 0 ? null : CreateSensor(random),
            Button = random.Next(2) == 0 ? null : new TestButton()
        };

        private static TestSensor CreateSensor(Random random) => new() { Gust = random.Next(10), Rain = random.Next(10), Power = random.Next(10) };
    }
}
