using HomeBlaze.Services;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>
/// A graph with a sensor at <c>/Weather</c> with a gust of 9 and the name WS90, blinds at <c>/Blinds</c>, a notifier at
/// <c>/Notifier</c> and a rule host at <c>/Host</c> whose rule <c>r1</c> is <see cref="Rule"/>.
/// </summary>
internal sealed class RuleGraph
{
    public RuleGraph()
    {
        var graph = new TestGraph();
        Host.Rules = new Dictionary<string, AutomationRule> { ["r1"] = Rule };
        graph.Add("Weather", Weather);
        graph.Add("Blinds", Blinds);
        graph.Add("Notifier", Notifier);
        graph.Add("Host", Host);
        Weather.Gust = 9;
        Weather.Name = "WS90";
        Resolver = graph.Resolver;
    }

    public SubjectPathResolver Resolver { get; }

    public TestSensor Weather { get; } = new();

    public TestBlinds Blinds { get; } = new();

    public TestNotifier Notifier { get; } = new();

    public TestRuleHost Host { get; } = new();

    public AutomationRule Rule { get; } = new();
}
