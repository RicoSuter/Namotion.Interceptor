using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

/// <summary>
/// Applies random sequences of structural changes with fixed seeds and checks after each batch that every automation,
/// which resolves its paths again only on a change on its graph route, is bound exactly as resolving every path of every
/// automation again would bind it: the properties of its triggers, its watched set, the sources of its event triggers
/// and its checkpoint path. A batch of several changes lets the later ones race with the workers that resolve the paths
/// after the earlier ones.
/// </summary>
public class AutomationGraphRouteDifferentialTests
{
    private const int Steps = 60;

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 16)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task WhenRandomStructuralChangesAreApplied_ThenEveryAutomationIsBoundAsAFullResolutionBindsIt(int seed)
    {
        // Arrange
        await using var world = await RandomWorld.CreateAsync(seed);
        var log = new List<string>();

        for (var step = 0; step < Steps; step++)
        {
            // Act
            var changes = world.Random.Next(1, 4);
            for (var change = 0; change < changes; change++)
            {
                log.Add($"{step}: {world.ApplyRandomChange()}");
            }

            await world.FlushAsync();

            // Assert
            if (world.FindDifference() is { } difference)
            {
                Assert.Fail($"Seed {seed}, step {step}: {difference}\n{string.Join('\n', log)}");
            }
        }
    }

    /// <summary>
    /// Rooms at <c>/R0</c> to <c>/R2</c> with sensors by key, by position and by reference and a button, folders
    /// <c>/F0</c>, <c>/F0/F2</c> and <c>/F1</c> of [InlinePaths] with a level, an unrelated holder, a rule host, four
    /// automations with the same rules, placed in the folders and the root, and one in <c>/F0/F2</c> with absolute paths
    /// only, whose checkpoint path changes when its folder moves, besides the automation of the host.
    /// </summary>
    private sealed class RandomWorld : IAsyncDisposable
    {
        private static readonly string[] RoomKeys = ["R0", "R1", "R2"];
        private static readonly string[] DeviceKeys = ["d0", "d1", "d2"];

        private readonly AutomationTestHost _host;
        private readonly TestFolder[] _folders;
        private readonly TestSensorHolder _holder = new();
        private readonly TestRuleHost _ruleHost = new();
        private readonly List<Automation> _automations = [];
        private readonly Dictionary<Automation, List<int>> _containers = [];

        private RandomWorld(AutomationTestHost host, int seed)
        {
            _host = host;
            Random = new Random(seed);
            _folders = [new TestFolder { Level = 3 }, new TestFolder { Level = 7 }, new TestFolder { Level = 9 }];
        }

        public Random Random { get; }

        private TestGraph Graph => _host.Graph;

        public static async Task<RandomWorld> CreateAsync(int seed)
        {
            var world = new RandomWorld(await AutomationTestHost.StartAsync(_ => { }), seed);
            await world.BuildAsync();
            return world;
        }

        public string ApplyRandomChange() => Random.Next(14) switch
        {
            0 => ChangeDevices(),
            1 => ChangeItems(),
            2 => ChangeSensor(),
            3 => ChangeButton(),
            4 => ChangeRoom(),
            5 => ChangeFolderChild(),
            6 or 7 => MoveAutomation(),
            8 => AddDynamicProperty(),
            9 => ChangeRuleHost(),
            10 => ChangeLevel(),
            11 => MoveFolder(),
            12 => AddDynamicHolder(),
            _ => ChangeUnrelated()
        };

        public async Task FlushAsync()
        {
            foreach (var automation in _automations.Append(_host.Automation))
            {
                await automation.FlushAsync();
            }
        }

        public string? FindDifference()
        {
            foreach (var automation in _automations.Append(_host.Automation))
            {
                if (FindDifference(automation) is { } difference)
                {
                    return $"{automation.Name}: {difference}";
                }
            }

            return null;
        }

        public ValueTask DisposeAsync() => _host.DisposeAsync();

        private string? FindDifference(Automation automation)
        {
            var resolver = Graph.Resolver;
            var runtime = automation.Runtime!;
            var path = resolver.GetPath(automation, PathStyle.Canonical);
            if (!string.Equals(runtime.Location.AutomationPath, path, StringComparison.Ordinal))
            {
                return $"checkpoint path {runtime.Location.AutomationPath} instead of {path}";
            }

            var watched = new HashSet<PropertyReference>();
            foreach (var rule in runtime.Engine.Rules.Where(rule => rule.IsActivated))
            {
                if (FindDifference(rule, watched) is { } difference)
                {
                    return $"rule {rule.Key}: {difference}";
                }
            }

            var published = runtime.Changes.Registration!.Watched;
            return published.SetEquals(watched)
                ? null
                : $"watched [{Describe(published)}] instead of [{Describe(watched)}]";
        }

        private string? FindDifference(CompiledRule rule, HashSet<PropertyReference> watched)
        {
            var hasPending = false;
            foreach (var trigger in rule.PropertyTriggers.Where(trigger => trigger.State.Phase != TriggerPhase.Inactive))
            {
                var expected = Resolve(trigger.PropertyPaths, rule.Rule);
                if (!expected.SequenceEqual(trigger.Dependencies))
                {
                    return $"{trigger.Description} reads [{Describe(trigger.Dependencies)}] instead of [{Describe(expected)}]";
                }

                watched.UnionWith(expected);
                hasPending |= trigger.State.Phase == TriggerPhase.Pending;
            }

            if (hasPending)
            {
                var expected = Resolve(rule.OnlyIfPropertyPaths, rule.Rule);
                if (!expected.SequenceEqual(rule.OnlyIfDependencies))
                {
                    return $"Only if reads [{Describe(rule.OnlyIfDependencies)}] instead of [{Describe(expected)}]";
                }

                watched.UnionWith(expected);
            }

            foreach (var trigger in rule.EventTriggers)
            {
                var expected = AutomationPaths.ResolveSubject(Graph.Resolver, trigger.Configuration.SubjectPath, rule.Rule);
                if (!ReferenceEquals(expected, trigger.Source))
                {
                    return $"{trigger.Description} has another source";
                }
            }

            return null;
        }

        private List<PropertyReference> Resolve(IReadOnlyList<string> paths, AutomationRule rule) =>
        [
            .. paths
                .Select(path => AutomationPaths.ResolveProperty(Graph.Resolver, path, rule)?.Reference)
                .OfType<PropertyReference>()
        ];

        private static string Describe(IEnumerable<PropertyReference> properties) =>
            string.Join(", ", properties.Select(property => $"{property.Subject.GetType().Name}.{property.Name}"));

        private async Task BuildAsync()
        {
            foreach (var key in RoomKeys)
            {
                Graph.Add(key, CreateRoom());
            }

            Graph.Add("F0", _folders[0]);
            Graph.Add("F1", _folders[1]);
            _folders[0].Add("F2", _folders[2]);
            Graph.Add("Holder", _holder);
            Graph.Add("RuleHost", _ruleHost);
            for (var index = 0; index < 4; index++)
            {
                var container = index == 3 ? -1 : index;
                var automation = await _host.AddAutomationAsync(
                    "A" + index, automation => automation.Rules = CreateRules(index == 3), automation => AddTo(container, automation));
                _automations.Add(automation);
                _containers[automation] = [container];
            }

            var absolute = await _host.AddAutomationAsync(
                "A4",
                automation => automation.Rules = Keyed(
                    ("keyed", On("Keyed", When("/R1/Devices[d2]/Gust > 5"), Invoke("/Blinds/Close"))),
                    ("extra", On("Extra", When("/F1/Extra/Gust > 5"), Invoke("/Blinds/Close")))),
                automation => _folders[2].Add("A4", automation));
            _automations.Add(absolute);
            _containers[absolute] = [2];
        }

        // The last automation also reads a level above its folder, whose route is unbounded.
        private static Dictionary<string, AutomationRule> CreateRules(bool readsAboveItsFolder)
        {
            var rules = Keyed(
                ("keyed", On("Keyed", When("/R0/Devices[d0]/Gust > 5"), Invoke("/Blinds/Close"))),
                ("mixed", On("Mixed", When("/R1/Items[1]/Rain > 5 and /R2/Sensor/Power > 5"), Invoke("/Blinds/Close"))),
                ("inline", On("Inline", When("/F0/S0/Gust > 5 or /F0/F2/S0/Rain > 5"), Invoke("/Blinds/Close"))),
                ("level", On("Level", When("../../Level > 5 and ../Enabled == true"), Invoke("/Blinds/Close"))),
                ("dynamic", On("Dynamic", When("/R0/Sensor/Wind > 5"), Invoke("/Blinds/Close"))),
                ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/R1/Button", "TestPressEvent")], [Invoke("/Blinds/Close")])),
                ("guarded", Create("Guarded", AutomationRuleMode.On, [When("/R2/Devices[d1]/Gust > 5")], [Invoke("/Blinds/Close")], onlyIf: "/R0/Items[0]/Power > 5")));
            if (readsAboveItsFolder)
            {
                rules["above"] = On("Above", When("../../../Level > 5"), Invoke("/Blinds/Close"));
            }

            return rules;
        }

        private TestRoom CreateRoom() => new()
        {
            Level = Random.Next(10),
            Devices = DeviceKeys.Where(_ => Random.Next(2) == 0).ToDictionary(key => key, _ => CreateSensor()),
            Items = [.. Enumerable.Range(0, Random.Next(4)).Select(_ => CreateSensor())],
            Sensor = Random.Next(3) == 0 ? null : CreateSensor(),
            Button = Random.Next(2) == 0 ? null : new TestButton()
        };

        private TestSensor CreateSensor() => new() { Gust = Random.Next(10), Rain = Random.Next(10), Power = Random.Next(10) };

        private TestRoom? PickRoom() => Graph.Root.Children.GetValueOrDefault(RoomKeys[Random.Next(RoomKeys.Length)]) as TestRoom;

        // A sensor that is in the graph already, so that it gets a second parent, or a new one.
        private TestSensor PickSensor()
        {
            if (Random.Next(3) == 0 && PickRoom() is { } room)
            {
                var existing = room.Devices.Values.Concat(room.Items).Append(room.Sensor).OfType<TestSensor>().ToList();
                if (existing.Count > 0)
                {
                    return existing[Random.Next(existing.Count)];
                }
            }

            return CreateSensor();
        }

        private string ChangeDevices()
        {
            if (PickRoom() is not { } room)
            {
                return "no room for devices";
            }

            var key = DeviceKeys[Random.Next(DeviceKeys.Length)];
            var devices = new Dictionary<string, TestSensor>(room.Devices);
            if (Random.Next(3) == 0)
            {
                devices.Remove(key);
            }
            else
            {
                devices[key] = PickSensor();
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
            var operation = Random.Next(3);
            if (operation == 0 && items.Count > 0)
            {
                items.RemoveAt(Random.Next(items.Count));
            }
            else if (operation == 1)
            {
                items.Reverse();
            }
            else
            {
                items.Insert(Random.Next(items.Count + 1), PickSensor());
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

            room.Sensor = Random.Next(3) == 0 ? null : PickSensor();
            return "sensor";
        }

        private string ChangeButton()
        {
            if (PickRoom() is not { } room)
            {
                return "no room for a button";
            }

            room.Button = Random.Next(2) == 0 ? null : new TestButton();
            return "button";
        }

        private string ChangeRoom()
        {
            var key = RoomKeys[Random.Next(RoomKeys.Length)];
            if (Random.Next(3) == 0)
            {
                Graph.Remove(key);
                return $"remove {key}";
            }

            Graph.Add(key, CreateRoom());
            return $"replace {key}";
        }

        private string ChangeFolderChild()
        {
            var folder = _folders[Random.Next(_folders.Length)];
            if (Random.Next(2) == 0)
            {
                folder.Remove("S0");
                return "remove S0";
            }

            folder.Add("S0", PickSensor());
            return "add S0";
        }

        // Adds the automation to another container and removes it from one it was in, except sometimes, which leaves it
        // with several parents until a later move.
        private string MoveAutomation()
        {
            var automation = _automations[Random.Next(4)];
            var containers = _containers[automation];
            var target = Random.Next(-1, _folders.Length);
            if (!containers.Contains(target))
            {
                AddTo(target, automation);
                containers.Add(target);
            }

            if (containers.Count > 1 && Random.Next(4) != 0)
            {
                var source = containers[Random.Next(containers.Count - 1)];
                RemoveFrom(source, automation);
                containers.Remove(source);
            }

            return $"move {automation.Name} to {target}, in [{string.Join(", ", containers)}]";
        }

        private string AddDynamicProperty()
        {
            if (PickRoom()?.Sensor is not { } sensor ||
                ((IInterceptorSubject)sensor).TryGetRegisteredSubject() is not { } registered ||
                registered.TryGetProperty("Wind") is not null)
            {
                return "no sensor for wind";
            }

            var wind = Random.Next(10);
            registered.AddProperty("Wind", typeof(double), _ => (double)wind, null);
            return $"wind {wind}";
        }

        // Moves the inner folder between the outer ones, also when an automation is in it.
        private string MoveFolder()
        {
            var inner = _folders[2];
            var (source, target) = _folders[0].Children.ContainsKey("F2") ? (_folders[0], _folders[1]) : (_folders[1], _folders[0]);
            target.Add("F2", inner);
            source.Remove("F2");
            return "move F2";
        }

        // A property added at run time in the middle of a path, which takes precedence over a child of [InlinePaths].
        private string AddDynamicHolder()
        {
            var registered = ((IInterceptorSubject)_folders[1]).TryGetRegisteredSubject()!;
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
            var automation = _automations[Random.Next(4)];
            _ruleHost.Rules = Random.Next(2) == 0
                ? new Dictionary<string, AutomationRule>()
                : new Dictionary<string, AutomationRule> { ["shared"] = automation.Rules["level"] };
            return $"rule host {_ruleHost.Rules.Count} of {automation.Name}";
        }

        private string ChangeLevel()
        {
            var folder = _folders[Random.Next(_folders.Length)];
            folder.Level = Random.Next(10);
            return "level";
        }

        private string ChangeUnrelated()
        {
            _holder.Sensor = Random.Next(2) == 0 ? null : CreateSensor();
            return "unrelated";
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
    }
}
