using System.Collections.Concurrent;
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

/// <summary>
/// Runs the same seeded scenario on two hosts, one with graph routes and one whose dispatcher treats every route as
/// unbounded, which is the dispatch without routes, and compares everything observable after each step: the statuses,
/// states, state changes and checkpoint locations of the automations, the runs of every rule and what the runs changed
/// in the graph. Any difference is a behavior that the routes changed.
/// </summary>
/// <remarks>
/// Each step is one change from the test followed by a flush of every automation, so that the inputs of a step are
/// queued before the workers process them; a move flushes between its two writes. The rules write only properties that
/// no other automation reads, so that the workers of a step do not race each other. Without routes every automation
/// receives every structural change, also one that cannot change what its paths resolve to, which differs observably
/// in two ways only, each kept out of the scenario by the constraint named here:
/// <list type="bullet">
/// <item>A structural write that a rule makes reaches every automation at the chain depth of the run, and beyond the
/// chain limit it is dropped and reported as a stopped feedback chain by an automation that takes no part in the chain,
/// unless its graph change was still queued, and counted among the dropped inputs of an automation in the chain whose
/// route does not have the written property. The structural writes of the rules here end after one run;
/// <see cref="WhenARuleReplacesTheSubjectItReadsEndlessly_ThenTheChainStopsAsWithoutGraphRoutesAndOnlyTheOtherAutomationsDiffer"/>
/// covers a chain beyond the limit.</item>
/// <item>A full queue drops inputs in their order, and without routes a queued graph change takes a place in it, so
/// that the number of dropped inputs and which ones differ. The step that overloads a queue writes no structure, and
/// the steps before it are flushed.</item>
/// </list>
/// A change that no write publishes, such as a list or dictionary changed in place or an attribute whose getter returns
/// another value, is observed without routes only at the next unrelated structural change; the scenario makes none.
/// </remarks>
public class AutomationGraphRouteDifferentialTests
{
    private const int Steps = 40;

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 50)];

    // The seeds after those of the unit tests, and long scenarios of their own seeds.
    public static TheoryData<int, int> IntegrationSeeds
    {
        get
        {
            var seeds = new TheoryData<int, int>();
            for (var seed = 51; seed <= 300; seed++)
            {
                seeds.Add(seed, Steps);
            }

            for (var seed = 1001; seed <= 1200; seed++)
            {
                seeds.Add(seed, Steps * 3);
            }

            return seeds;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public Task WhenRandomChangesAreApplied_ThenEveryAutomationBehavesAsWithoutGraphRoutes(int seed) => CompareAsync(seed, Steps);

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(IntegrationSeeds))]
    public Task WhenManyRandomChangesAreApplied_ThenEveryAutomationBehavesAsWithoutGraphRoutes(int seed, int steps) => CompareAsync(seed, steps);

    // The replacing automation stops its chain as without routes. Without routes the others receive the structural writes
    // of the chain too, and one that takes each graph change before the next arrives receives one beyond the limit and
    // reports a stopped chain that it takes no part in; one whose graph change is still queued coalesces the later ones.
    [Fact]
    public async Task WhenARuleReplacesTheSubjectItReadsEndlessly_ThenTheChainStopsAsWithoutGraphRoutesAndOnlyTheOtherAutomationsDiffer()
    {
        // Arrange
        await using var routed = await ReplacingWorld.CreateAsync(makesEveryRouteUnbounded: false);
        await using var unrouted = await ReplacingWorld.CreateAsync(makesEveryRouteUnbounded: true);

        // Act
        await routed.StartChainAsync();
        await unrouted.StartChainAsync();

        // Assert
        Assert.Equal(unrouted.DescribeReplacing(), routed.DescribeReplacing());
        Assert.StartsWith("A feedback chain of more than 16 inputs was stopped", routed.Replacing.StatusMessage);
        Assert.Equal(routed.DescribeOthers(), unrouted.DescribeOthers(withoutChainReport: true));
        Assert.All(routed.Others, other => Assert.Null(other.StatusMessage));
    }

    private static async Task CompareAsync(int seed, int steps)
    {
        // Arrange
        await using var routed = await World.CreateAsync(seed, makesEveryRouteUnbounded: false);
        await using var unrouted = await World.CreateAsync(seed, makesEveryRouteUnbounded: true);
        var log = new List<string>();
        var filteredSteps = 0;
        AssertSame(routed, unrouted, seed, -1, log);

        for (var step = 0; step < steps; step++)
        {
            // Act
            var isBounded = routed.GetBoundedAutomations();
            var routedBefore = routed.GetResolutionCounts();
            var unroutedBefore = unrouted.GetResolutionCounts();
            var description = await routed.ApplyRandomStepAsync();
            var unroutedDescription = await unrouted.ApplyRandomStepAsync();
            log.Add($"{step}: {description}");
            Assert.Equal(description, unroutedDescription);
            await routed.FlushAsync();
            await unrouted.FlushAsync();

            // Assert
            AssertSame(routed, unrouted, seed, step, log);
            var routedResolutions = routed.GetResolutionCounts();
            var unroutedResolutions = unrouted.GetResolutionCounts();
            if (routedResolutions.Sum() - routedBefore.Sum() < unroutedResolutions.Sum() - unroutedBefore.Sum())
            {
                filteredSteps++;
            }

            // The unrelated holder is on no route, so an automation on a bounded route neither receives its change nor
            // resolves a path.
            if (description == "unrelated")
            {
                for (var index = 0; index < isBounded.Length; index++)
                {
                    Assert.True(
                        !isBounded[index] || routedResolutions[index] == routedBefore[index],
                        $"Seed {seed}, step {step}: A{index} resolved paths after a change off its route.\n{string.Join('\n', log)}");
                }
            }
        }

        Assert.True(filteredSteps > 0, $"Seed {seed}: no step was filtered.\n{string.Join('\n', log)}");
    }

    private static void AssertSame(World routed, World unrouted, int seed, int step, List<string> log)
    {
        var expected = unrouted.Observe();
        var actual = routed.Observe();
        if (expected == actual)
        {
            return;
        }

        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var index = 0;
        while (index < expectedLines.Length && index < actualLines.Length && expectedLines[index] == actualLines[index])
        {
            index++;
        }

        Assert.Fail(
            $"Seed {seed}, step {step}: with routes\n  {actualLines.ElementAtOrDefault(index)}\nwithout routes\n  {expectedLines.ElementAtOrDefault(index)}\n" +
            string.Join('\n', log));
    }

    /// <summary>
    /// Rooms at <c>/R0</c> to <c>/R2</c> with sensors by key, by position and by reference and a button, folders
    /// <c>/F0</c>, <c>/F0/F2</c> and <c>/F1</c> of [InlinePaths] with a level, an unrelated holder, a rule host, and three
    /// automations with the states Day and Night, one in each folder at first, each with a random part of the rules, so
    /// that the route of one rule does not hide a missing property in the route of another. Each automation has a sensor
    /// <c>/C{i}</c> for its feedback chains and a replacer <c>/P{i}</c> whose operation replaces its sensor, which no other
    /// automation reads.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        private const int AutomationCount = 3;
        private static readonly string[] RoomKeys = ["R0", "R1", "R2"];
        private static readonly string[] DeviceKeys = ["d0", "d1", "d2"];

        private readonly AutomationTestHost _host;
        private readonly Random _random;
        private readonly TestFolder[] _folders = [new() { Level = 3 }, new() { Level = 7 }, new() { Level = 9 }];
        private readonly TestSensorHolder _holder = new();
        private readonly TestRuleHost _ruleHost = new();
        private readonly TestSensor[] _chains = [.. Enumerable.Range(0, AutomationCount).Select(_ => new TestSensor())];
        private readonly TestReplacer[] _replacers = [.. Enumerable.Range(0, AutomationCount).Select(_ => new TestReplacer { Sensor = new TestSensor { Gust = 1 } })];
        private readonly List<Automation> _automations = [];
        private readonly Dictionary<Automation, List<int>> _containers = [];
        private readonly Dictionary<Automation, ConcurrentQueue<string>> _stateChanges = [];
        private readonly List<RegisteredSubjectProperty> _winds = [];
        private bool _hasOverloaded;

        private World(AutomationTestHost host, int seed)
        {
            _host = host;
            _random = new Random(seed);
        }

        /// <summary>Gets how many stored results each automation resolved so far.</summary>
        public int[] GetResolutionCounts() => [.. _automations.Select(automation => automation.Runtime!.Routes.ResolutionCount)];

        /// <summary>Gets whether the union of the routes of each automation is bounded.</summary>
        public bool[] GetBoundedAutomations() => [.. _automations.Select(automation => !automation.Runtime!.Routes.IsUnbounded)];

        private TestGraph Graph => _host.Graph;

        public static async Task<World> CreateAsync(int seed, bool makesEveryRouteUnbounded)
        {
            var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
            var world = new World(host, seed);
            await world.BuildAsync();
            return world;
        }

        public async Task<string> ApplyRandomStepAsync()
        {
            if (!_hasOverloaded && _random.Next(40) == 0)
            {
                return await OverloadAsync();
            }

            return _random.Next(25) switch
            {
                0 => ChangeDevices(),
                1 => ChangeItems(),
                2 => ChangeSensor(),
                3 => ChangeButton(),
                4 => ChangeRoom(),
                5 => ChangeFolderChild(),
                6 => await MoveAutomationAsync(),
                7 => AddWind(),
                8 => SetWind(),
                9 => ChangeRuleHost(),
                10 => await MoveFolderAsync(),
                11 => AddExtra(),
                12 => ChangeUnrelated(),
                13 or 14 => WriteValue(),
                15 => ChangeLevel(),
                16 => Press(),
                17 => StartChain(),
                18 => StartReplacement(),
                19 => Advance(),
                20 => Touch(),
                21 => await ChangeConfigurationAsync(),
                22 => DetachFolder(),
                _ => WriteValue()
            };
        }

        public async Task FlushAsync()
        {
            // A second pass processes the graph changes that structural writes of rules in later automations queued.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var automation in _automations)
                {
                    await automation.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }

        /// <summary>Describes everything observable of the automations and of the graph that their rules write.</summary>
        public string Observe()
        {
            var text = new StringBuilder();
            foreach (var automation in _automations)
            {
                text.Append(automation.Name).Append(": ").Append(automation.Status).Append(", ").Append(automation.StatusMessage)
                    .Append(", state ").Append(automation.CurrentState).Append(" since ").Append(automation.StateEnteredAt)
                    .Append(", at ").Append(automation.Runtime?.Location.AutomationPath)
                    .Append(", changes [").AppendJoin("; ", _stateChanges[automation]).Append(']').Append('\n');
                foreach (var (key, rule) in automation.Rules.OrderBy(rule => rule.Key, StringComparer.Ordinal))
                {
                    text.Append("  ").Append(key).Append(": ").Append(rule.Status).Append(", runs ").Append(rule.RunCount)
                        .Append(", ").Append(rule.LastRunOutcome).Append(", ").Append(rule.LastRunMessage)
                        .Append(", triggered ").Append(rule.LastTriggeredAt).Append('\n');
                }
            }

            for (var index = 0; index < AutomationCount; index++)
            {
                text.Append("C").Append(index).Append(": ").Append(_chains[index].Name).Append(", ").Append(_chains[index].Power)
                    .Append("; P").Append(index).Append(": ").Append(_replacers[index].Replacements).Append(", ")
                    .Append(_replacers[index].Sensor?.Gust).Append('\n');
            }

            text.Append("Blinds: ").Append(_host.Blinds.CloseCount).Append(", ").Append(_host.Blinds.OpenCount)
                .Append(", now ").Append(_host.Time.GetUtcNow());
            return text.ToString();
        }

        public ValueTask DisposeAsync()
        {
            _host.Blinds.Release();
            return _host.DisposeAsync();
        }

        private async Task BuildAsync()
        {
            foreach (var key in RoomKeys)
            {
                Graph.Add(key, CreateRoom());
            }

            Graph.Add("F0", _folders[0]);
            Graph.Add("F1", _folders[1]);
            _folders[0].Add("F2", _folders[2]);
            _folders[1].Add("Button", new TestButton());
            Graph.Add("Holder", _holder);
            Graph.Add("RuleHost", _ruleHost);
            for (var index = 0; index < AutomationCount; index++)
            {
                Graph.Add("C" + index, _chains[index]);
                Graph.Add("P" + index, _replacers[index]);
            }

            for (var index = 0; index < AutomationCount; index++)
            {
                var container = index;
                var rules = ChooseRules(CreateRules(index));
                var automation = await _host.AddAutomationAsync(
                    "A" + index,
                    automation =>
                    {
                        automation.States = ["Day", "Night"];
                        automation.InitialState = "Day";
                        automation.Rules = rules;
                    },
                    automation => AddTo(container, automation));
                var changes = new ConcurrentQueue<string>();
                automation.Subscribe(new StateChangeRecorder(changes));
                _stateChanges[automation] = changes;
                _automations.Add(automation);
                _containers[automation] = [container];
            }

            await FlushAsync();
        }

        // Every family of paths: absolute paths through references, lists, dictionaries and [InlinePaths], paths relative to
        // the rule and up through the automation, a dynamic property and a dynamic holder, rearm, a hold, Only if, the clock,
        // event triggers on a device, on a sibling of the folder and on the automation, states, feedback chains of values and
        // a structural write of a rule.
        private static Dictionary<string, AutomationRule> CreateRules(int index)
        {
            var chain = $"/C{index}";
            var replacer = $"/P{index}";
            var rules = Keyed(
                ("keyed", Create("Keyed", AutomationRuleMode.DryRun, [When("/R0/Devices[d0]/Gust > 5")], [Set($"{chain}/Rain", "{{ /R0/Devices[d0]/Gust }}")])),
                ("mixed", On("Mixed", When("/R1/Items[1]/Rain > 5 and /R2/Sensor/Power > 5"), Invoke("/Blinds/Close"))),
                ("inline", On("Inline", When("/F0/S0/Gust > 5 or /F0/F2/S0/Rain > 5"), Invoke("/Blinds/Open"))),
                ("level", Create("Level", AutomationRuleMode.DryRun, [When("../../Level > 5 and ../Enabled == true")], [Invoke("/Blinds/Close")])),
                ("dynamic", On("Dynamic", When("/R0/Sensor/Wind > 5"), Invoke("/Blinds/Close"))),
                ("extra", Create("Extra", AutomationRuleMode.DryRun, [When("/F1/Extra/Gust > 5")], [Invoke("/Blinds/Close")])),
                ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/R1/Button", "TestPressEvent")], [Invoke("/Blinds/Open")])),
                ("sibling", Create("Sibling", AutomationRuleMode.DryRun, [OnEvent("../../Button", "TestPressEvent")], [Invoke("/Blinds/Open")])),
                ("entered", Create("Entered", AutomationRuleMode.DryRun, [OnEvent("..", "AutomationStateChangedEvent")], [Invoke("/Blinds/Open")])),
                ("guarded", Create("Guarded", AutomationRuleMode.On, [When("/R2/Devices[d1]/Gust > 5")], [Invoke("/Blinds/Close")], onlyIf: "/R0/Items[0]/Power > 5")),
                ("rearm", On("Rearm", When("/R1/Sensor/Gust > 5", rearm: "/R2/Sensor/Rain < 3"), Invoke("/Blinds/Close"))),
                ("hold", On("Hold", When("/R0/Sensor/Rain > 5", hold: TimeSpan.FromSeconds(10)), Invoke("/Blinds/Open"))),
                ("stale", Create("Stale", AutomationRuleMode.DryRun, [When("now - /R1/Sensor/LastUpdated > 1m")], [Invoke("/Blinds/Close")])),
                ("self", Create("Self", AutomationRuleMode.DryRun, [When("./RunCount > 2 or ../CurrentState == Night")], [Invoke("/Blinds/Close")])),
                ("night", Create("Night", AutomationRuleMode.On, [When("/R0/Devices[d1]/Rain > 5")], [Invoke("/Blinds/Close")], inState: "Night")),
                ("toNight", Create("To night", AutomationRuleMode.On, [When("/R2/Level > 4")], [Enter("Night")], inState: "Day")),
                ("toDay", Create("To day", AutomationRuleMode.On, [When("/R2/Level <= 4")], [Enter("Day")], inState: "Night")),
                ("ping", On("Ping", When($"{chain}/Name == ping"), Set($"{chain}/Name", "pong"))),
                ("pong", On("Pong", When($"{chain}/Name == pong"), Set($"{chain}/Name", "ping"))),
                ("a", On("A", When($"{chain}/Name == a"), Set($"{chain}/Name", "b"))),
                ("b", On("B", When($"{chain}/Name == b"), Set($"{chain}/Name", "c"))),
                ("c", On("C", When($"{chain}/Name == c"), Set($"{chain}/Name", "d"), Set($"{chain}/Power", "{{ /R0/Sensor/Gust }}"))),
                ("replace", On("Replace", When($"{replacer}/Sensor/Gust > 8"), Invoke($"{replacer}/Replace"))));
            // The other automations stay on bounded routes.
            if (index == 2)
            {
                rules["above"] = Create("Above", AutomationRuleMode.DryRun, [When("../../../Level > 5")], [Invoke("/Blinds/Close")]);
            }

            if (index == 0)
            {
                rules["wait"] = On("Wait", When("/Weather/Rain > 0"), Invoke("/Blinds/Wait"));
                rules["gust"] = Create("Gust", AutomationRuleMode.DryRun, [When("/Weather/Gust > 1050")], [Invoke("/Blinds/Close")]);
            }

            return rules;
        }

        // Whole groups, since the rules of a group drive each other, and the rules that overload the queue in the first
        // automation always.
        private Dictionary<string, AutomationRule> ChooseRules(Dictionary<string, AutomationRule> rules)
        {
            string[][] groups =
            [
                ["keyed"], ["mixed"], ["inline"], ["level"], ["dynamic"], ["extra"], ["press"], ["sibling"], ["entered"], ["guarded"],
                ["rearm"], ["hold"], ["stale"], ["self"], ["night", "toNight", "toDay"], ["ping", "pong"], ["a", "b", "c"], ["replace"],
                ["above"]
            ];
            var chosen = new HashSet<string>(["wait", "gust"]);
            foreach (var group in groups)
            {
                if (_random.Next(5) < 3)
                {
                    chosen.UnionWith(group);
                }
            }

            return rules.Where(rule => chosen.Contains(rule.Key)).ToDictionary();
        }

        private TestRoom CreateRoom() => new()
        {
            Level = _random.Next(10),
            Devices = DeviceKeys.Where(_ => _random.Next(2) == 0).ToDictionary(key => key, _ => CreateSensor()),
            Items = [.. Enumerable.Range(0, _random.Next(4)).Select(_ => CreateSensor())],
            Sensor = _random.Next(3) == 0 ? null : CreateSensor(),
            Button = _random.Next(2) == 0 ? null : new TestButton()
        };

        private TestSensor CreateSensor() => new() { Gust = _random.Next(10), Rain = _random.Next(10), Power = _random.Next(10) };

        private TestRoom? PickRoom() => Graph.Root.Children.GetValueOrDefault(RoomKeys[_random.Next(RoomKeys.Length)]) as TestRoom;

        private List<TestSensor> SensorsOf(TestRoom room) =>
            [.. room.Devices.Values.Concat(room.Items).Append(room.Sensor).OfType<TestSensor>()];

        // A sensor that is in the graph already, often in the room it is written to, so that it gets a second parent or
        // another key or position in the same collection, or a new one.
        private TestSensor PickSensor(TestRoom? target = null)
        {
            if (_random.Next(3) == 0 && (target is not null && _random.Next(2) == 0 ? target : PickRoom()) is { } room &&
                SensorsOf(room) is { Count: > 0 } existing)
            {
                return existing[_random.Next(existing.Count)];
            }

            return CreateSensor();
        }

        private string ChangeDevices()
        {
            if (PickRoom() is not { } room)
            {
                return "no room for devices";
            }

            var key = DeviceKeys[_random.Next(DeviceKeys.Length)];
            var devices = new Dictionary<string, TestSensor>(room.Devices);
            if (_random.Next(3) == 0)
            {
                devices.Remove(key);
            }
            else
            {
                devices[key] = PickSensor(room);
            }

            room.Devices = devices;
            return $"devices {key}";
        }

        private string ChangeItems()
        {
            if (PickRoom() is not { } room)
            {
                return "no room for items";
            }

            var items = new List<TestSensor>(room.Items);
            var operation = _random.Next(3);
            if (operation == 0 && items.Count > 0)
            {
                items.RemoveAt(_random.Next(items.Count));
            }
            else if (operation == 1)
            {
                items.Reverse();
            }
            else
            {
                items.Insert(_random.Next(items.Count + 1), PickSensor(room));
            }

            room.Items = items;
            return $"items {operation}";
        }

        private string ChangeSensor()
        {
            if (PickRoom() is not { } room)
            {
                return "no room for a sensor";
            }

            room.Sensor = _random.Next(3) == 0 ? null : PickSensor(room);
            return "sensor";
        }

        private string ChangeButton()
        {
            if (PickRoom() is not { } room)
            {
                return "no room for a button";
            }

            room.Button = _random.Next(2) == 0 ? null : new TestButton();
            return "button";
        }

        private string ChangeRoom()
        {
            var key = RoomKeys[_random.Next(RoomKeys.Length)];
            if (_random.Next(3) == 0)
            {
                Graph.Remove(key);
                return $"remove {key}";
            }

            Graph.Add(key, CreateRoom());
            return $"replace {key}";
        }

        private string ChangeFolderChild()
        {
            var folder = _random.Next(_folders.Length);
            if (_random.Next(2) == 0)
            {
                _folders[folder].Remove("S0");
                return $"remove S0 of F{folder}";
            }

            _folders[folder].Add("S0", PickSensor());
            return $"add S0 to F{folder}";
        }

        // Adds the automation to another container and removes it from one it was in, except sometimes, which leaves it
        // with several parents until a later move. The automations process the addition before the removal, which they
        // otherwise race: one that resolves between the two writes sees the subject with both parents, and a move starts
        // it fresh.
        private async Task<string> MoveAutomationAsync()
        {
            var automation = _automations[_random.Next(_automations.Count)];
            var containers = _containers[automation];
            var target = _random.Next(-1, _folders.Length);
            if (!containers.Contains(target))
            {
                AddTo(target, automation);
                containers.Add(target);
                await FlushAsync();
            }

            if (containers.Count > 1 && _random.Next(4) != 0)
            {
                var source = containers[_random.Next(containers.Count - 1)];
                RemoveFrom(source, automation);
                containers.Remove(source);
            }

            return $"move {automation.Name} to {target}, in [{string.Join(", ", containers)}]";
        }

        // A property that holds no subjects, added at run time with a value or with none.
        private string AddWind()
        {
            if (PickRoom()?.Sensor is not { } sensor ||
                ((IInterceptorSubject)sensor).TryGetRegisteredSubject() is not { } registered ||
                registered.TryGetProperty("Wind") is not null)
            {
                return "no sensor for wind";
            }

            var wind = _random.Next(2) == 0 ? null : (double?)_random.Next(10);
            _winds.Add(registered.AddProperty("Wind", typeof(double?), _ => wind, (_, value) => wind = (double?)value));
            return $"wind {wind?.ToString() ?? "null"}";
        }

        private string SetWind()
        {
            if (_winds.Count == 0)
            {
                return "no wind to set";
            }

            var value = _random.Next(10);
            _winds[_random.Next(_winds.Count)].SetValue((double)value);
            return $"set wind {value}";
        }

        // A property added at run time in the middle of a path, which takes precedence over a child of [InlinePaths].
        private string AddExtra()
        {
            if (((IInterceptorSubject)_folders[1]).TryGetRegisteredSubject() is not { } registered)
            {
                return "no attached folder for extra";
            }

            if (registered.TryGetProperty("Extra") is not null)
            {
                return "extra exists";
            }

            var sensor = CreateSensor();
            registered.AddProperty("Extra", typeof(TestSensor), _ => sensor, null);
            return $"extra {sensor.Gust}";
        }

        private string ChangeRuleHost()
        {
            var automation = _automations[_random.Next(_automations.Count)];
            var shared = automation.Rules.GetValueOrDefault("level") ?? automation.Rules.MinBy(rule => rule.Key, StringComparer.Ordinal).Value;
            _ruleHost.Rules = _random.Next(2) == 0
                ? new Dictionary<string, AutomationRule>()
                : new Dictionary<string, AutomationRule> { ["shared"] = shared };
            return $"rule host {_ruleHost.Rules.Count} of {automation.Name}";
        }

        // Moves the inner folder between the outer ones, also when an automation is in it, with the addition processed
        // before the removal as for a move of an automation.
        private async Task<string> MoveFolderAsync()
        {
            var inner = _folders[2];
            var (source, target) = _folders[0].Children.ContainsKey("F2") ? (_folders[0], _folders[1]) : (_folders[1], _folders[0]);
            target.Add("F2", inner);
            await FlushAsync();
            source.Remove("F2");
            return "move F2";
        }

        // Detaches an outer folder from the root, which gives an automation in it and in another container a single parent
        // without a change of a property that holds the automation, or attaches it again.
        private string DetachFolder()
        {
            var index = _random.Next(2);
            var key = "F" + index;
            if (Graph.Root.Children.ContainsKey(key))
            {
                Graph.Remove(key);
                return $"detach {key}";
            }

            Graph.Add(key, _folders[index]);
            return $"attach {key}";
        }

        private string ChangeUnrelated()
        {
            _holder.Sensor = _holder.Sensor is null ? CreateSensor() : null;
            return "unrelated";
        }

        private string WriteValue()
        {
            if (PickRoom() is not { } room || SensorsOf(room) is not { Count: > 0 } sensors)
            {
                return "no sensor to write";
            }

            var sensor = sensors[_random.Next(sensors.Count)];
            var value = _random.Next(10);
            switch (_random.Next(3))
            {
                case 0:
                    sensor.Gust = value;
                    return $"gust {value}";
                case 1:
                    sensor.Rain = value;
                    return $"rain {value}";
                default:
                    sensor.Power = value;
                    return $"power {value}";
            }
        }

        private string ChangeLevel()
        {
            var value = _random.Next(10);
            if (_random.Next(2) == 0 && PickRoom() is { } room)
            {
                room.Level = value;
                return $"room level {value}";
            }

            var folder = _random.Next(_folders.Length);
            _folders[folder].Level = value;
            return $"level of F{folder} {value}";
        }

        private string Press()
        {
            if (_random.Next(2) == 0)
            {
                ((TestButton)_folders[1].Children["Button"]).Press("single");
                return "press the button of F1";
            }

            if (PickRoom()?.Button is not { } button)
            {
                return "no button to press";
            }

            button.Press("single");
            return "press";
        }

        private string StartChain()
        {
            var index = _random.Next(AutomationCount);
            var name = _random.Next(3) switch
            {
                0 => "ping",
                1 => "a",
                _ => "idle"
            };
            _chains[index].Name = name;
            return $"chain {index} {name}";
        }

        private string StartReplacement()
        {
            var index = _random.Next(AutomationCount);
            _replacers[index].Sensor!.Gust = 9;
            return $"replacement {index}";
        }

        private string Advance()
        {
            var seconds = _random.Next(1, 40);
            _host.Time.Advance(TimeSpan.FromSeconds(seconds));
            return $"advance {seconds}s";
        }

        private string Touch()
        {
            if (PickRoom()?.Sensor is not { } sensor)
            {
                return "no sensor to touch";
            }

            sensor.LastUpdated = _host.Time.GetUtcNow();
            return "touch";
        }

        private async Task<string> ChangeConfigurationAsync()
        {
            var automation = _automations[_random.Next(_automations.Count)];
            switch (_random.Next(3))
            {
                case 0:
                    automation.Enabled = !automation.Enabled;
                    await automation.ApplyConfigurationAsync(CancellationToken.None);
                    return $"{automation.Name} enabled {automation.Enabled}";
                case 1:
                    automation.ResetState();
                    return $"{automation.Name} reset";
                default:
                    // A Save replaces the rule instances, so that paths relative to a rule start at another subject. The
                    // graph change of the write is processed before the configuration is applied, which it otherwise races.
                    automation.Rules = automation.Rules.ToDictionary(
                        rule => rule.Key,
                        rule => new AutomationRule
                        {
                            Name = rule.Value.Name,
                            Mode = rule.Value.Mode,
                            InState = rule.Value.InState,
                            Triggers = [.. rule.Value.Triggers],
                            OnlyIf = rule.Value.OnlyIf,
                            Actions = [.. rule.Value.Actions]
                        });
                    await FlushAsync();
                    await automation.ApplyConfigurationAsync(CancellationToken.None);
                    return $"{automation.Name} saved copies";
            }
        }

        // The first automation waits in an operation while the queue overflows with changes of a property it watches; a
        // disabled automation runs no operation.
        private async Task<string> OverloadAsync()
        {
            _hasOverloaded = true;
            if (!_automations[0].Enabled)
            {
                return "no overload while disabled";
            }

            _host.Weather.Rain = 1;
            await AsyncTestHelpers.WaitUntilAsync(() => _host.Blinds.IsWaiting);
            for (var gust = 1; gust <= AutomationInputQueue.Capacity + 100; gust++)
            {
                _host.Weather.Gust = gust;
            }

            // Written while the queue is full, so that the drop does not race with the worker that resumes.
            _host.Weather.Rain = 0;
            _host.Blinds.Release();
            return "overload";
        }

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

        private sealed class StateChangeRecorder(ConcurrentQueue<string> changes) : IObserver<AutomationStateChangedEvent>
        {
            public void OnNext(AutomationStateChangedEvent value) => changes.Enqueue($"{value.PreviousState}>{value.NewState}@{value.Timestamp:O}");

            public void OnError(Exception error)
            {
                // The automation never fails its observers.
            }

            public void OnCompleted()
            {
                // The automation never completes its observers.
            }
        }
    }

    /// <summary>
    /// An automation whose rules replace the sensor of <c>/Replacer</c> when its gust is high and when it is low, a
    /// structural write on their route, and two automations whose rules read only <c>/Weather</c>.
    /// </summary>
    private sealed class ReplacingWorld : IAsyncDisposable
    {
        private const string ChainReport = "A feedback chain of more than 16 inputs was stopped";

        private readonly AutomationTestHost _host;
        private readonly TestReplacer _replacer = new() { Sensor = new TestSensor { Gust = 5 } };

        private ReplacingWorld(AutomationTestHost host)
        {
            _host = host;
        }

        public Automation Replacing { get; private set; } = null!;

        public List<Automation> Others { get; } = [];

        public static async Task<ReplacingWorld> CreateAsync(bool makesEveryRouteUnbounded)
        {
            var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
                ("gusts", On("Gusts", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))),
                makesEveryRouteUnbounded: makesEveryRouteUnbounded);
            var world = new ReplacingWorld(host);
            host.Graph.Add("Replacer", world._replacer);
            world.Others.Add(host.Automation);
            world.Others.Add(await host.AddAutomationAsync("Rain", automation => automation.Rules = Keyed(
                ("rain", On("Rain", When("/Weather/Rain > 0"), Invoke("/Blinds/Open"))))));
            world.Replacing = await host.AddAutomationAsync("Replacing", automation => automation.Rules = Keyed(
                ("high", On("High", When("/Replacer/Sensor/Gust > 8"), Invoke("/Replacer/Replace"))),
                ("low", On("Low", When("/Replacer/Sensor/Gust < 5"), Invoke("/Replacer/Replace")))));
            await world.FlushAsync();
            return world;
        }

        public async Task StartChainAsync()
        {
            _replacer.Sensor!.Gust = 9;
            await FlushAsync();
            await FlushAsync();
        }

        public string DescribeReplacing() =>
            $"{Describe(Replacing, withoutChainReport: false)}; replacements {_replacer.Replacements}, gust {_replacer.Sensor?.Gust}";

        public string DescribeOthers(bool withoutChainReport = false) =>
            string.Join('\n', Others.Select(other => Describe(other, withoutChainReport))) +
            $"\nBlinds: {_host.Blinds.CloseCount}, {_host.Blinds.OpenCount}";

        public ValueTask DisposeAsync() => _host.DisposeAsync();

        private static string Describe(Automation automation, bool withoutChainReport)
        {
            var isChainReport = withoutChainReport && automation.StatusMessage?.StartsWith(ChainReport, StringComparison.Ordinal) == true;
            var status = isChainReport ? $"{ServiceStatus.Running}, " : $"{automation.Status}, {automation.StatusMessage}";
            return $"{automation.Name}: {status}, " + string.Join("; ", automation.Rules.OrderBy(rule => rule.Key, StringComparer.Ordinal)
                .Select(rule => $"{rule.Key}: {rule.Value.Status}, runs {rule.Value.RunCount}, {rule.Value.LastRunOutcome}"));
        }

        private async Task FlushAsync()
        {
            foreach (var automation in Others.Append(Replacing))
            {
                await automation.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }
}
