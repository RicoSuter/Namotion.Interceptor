using System.Text.RegularExpressions;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// The configuration of an automation while it is edited: read once from the automation, changed only here, and written
/// to the automation as a whole on save. It holds no runtime state, so a save never replaces the current state or the
/// runs of a rule.
/// </summary>
public sealed class AutomationDraft
{
    private readonly Dictionary<string, AutomationValidationError> _inputErrors = new(StringComparer.Ordinal);
    private Automation _automation = null!;

    private AutomationDraft()
    {
    }

    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The roles of the automation, see <see cref="Automation.Roles"/>.</summary>
    public IReadOnlyList<string> Roles { get; set; } = [];

    public List<string> States { get; } = [];

    public string? InitialState { get; set; }

    /// <summary>The rules in the order they process an input.</summary>
    public List<RuleDraft> Rules { get; } = [];

    public static AutomationDraft From(Automation automation)
    {
        var draft = new AutomationDraft { Name = automation.Name, Enabled = automation.Enabled, InitialState = automation.InitialState, Roles = [.. automation.Roles], _automation = automation };
        draft.States.AddRange(automation.States);
        draft.Rules.AddRange(automation.GetRulesInOrder().Select(pair => RuleDraft.From(pair.Key, pair.Value)));
        return draft;
    }

    /// <summary>
    /// Writes the configuration to the automation, the rules last and the roles before them, so that the automation reads a
    /// complete configuration when it applies it and never runs new rules with old roles. New rule instances take the run properties of the instances they replace.
    /// </summary>
    public void ApplyTo(Automation automation)
    {
        automation.Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        automation.Enabled = Enabled;
        automation.Roles = [.. Roles];
        automation.States = [.. States];
        automation.InitialState = States.Count == 0 ? null : InitialState;
        automation.RuleOrder = [.. Rules.Select(rule => rule.Key)];
        automation.Rules = Rules.ToDictionary(rule => rule.Key, rule => rule.ToRule(path => ActionInputs.GetMethod(automation, rule.Key, path)));
    }

    /// <summary>The errors of the configuration as the automation and the editor check it, and the errors of inputs that do not parse.</summary>
    public IReadOnlyList<AutomationValidationError> Validate() =>
    [
        .. AutomationValidator.Validate(
            _automation,
            Roles,
            States,
            States.Count == 0 ? null : InitialState,
            Rules.ToDictionary(rule => rule.Key, rule => rule.ToRule(path => ActionInputs.GetMethod(_automation, rule.Key, path))),
            [.. Rules.Select(rule => rule.Key)]),
        .. CheckActionInputs(),
        .. _inputErrors.Values
    ];

    // The inputs of the actions are checked against the operations and properties they target, wherever the editor is showing.
    private IEnumerable<AutomationValidationError> CheckActionInputs() =>
        Rules.SelectMany(rule =>
        {
            var expressions = new RuleExpressions(_automation, rule.Key, rule.AllowsEvent);
            return rule.Actions.SelectMany((item, index) => ActionInputs.Check(item.Value, _automation, rule.Key, expressions)
                .Select(error => new AutomationValidationError(rule.Key, $"{ActionInputs.Name(item.Value, index)}: {error.Label}: {error.Error}")));
        });

    /// <summary>Increases whenever an input error is set or cleared, so that a cached result of <see cref="Validate"/> can tell it is stale.</summary>
    public int InputErrorVersion { get; private set; }

    /// <summary>Sets or clears the error of an input whose text does not parse, such as a duration, so that the draft cannot be saved meanwhile.</summary>
    public void SetInputError(string inputId, AutomationValidationError? error)
    {
        var isChanged = error is null
            ? _inputErrors.Remove(inputId)
            : !_inputErrors.TryGetValue(inputId, out var previous) || previous != error;
        if (error is not null)
        {
            _inputErrors[inputId] = error;
        }

        if (isChanged)
        {
            InputErrorVersion++;
        }
    }

    public RuleDraft? FindRule(string? key) => key is null ? null : Rules.Find(rule => rule.Key == key);

    /// <summary>Adds a rule named "Rule n" with a number that no other rule has, as the editor compares names without regard to case.</summary>
    public RuleDraft AddRule()
    {
        var number = Rules.Count + 1;
        while (Rules.Exists(rule => string.Equals(rule.Name?.Trim(), $"Rule {number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        var added = RuleDraft.Create(DraftKeys.NewRuleKey(Rules.Select(existing => existing.Key)), $"Rule {number}");
        Rules.Add(added);
        return added;
    }

    public void RemoveRule(RuleDraft rule) => Rules.Remove(rule);

    public void MoveRule(RuleDraft rule, int offset) => DraftLists.Move(Rules, rule, offset);

    /// <summary>Adds a state named "State n"; the first state of an automation becomes its initial state.</summary>
    public string AddState()
    {
        var number = States.Count + 1;
        while (States.Contains($"State {number}"))
        {
            number++;
        }

        var state = $"State {number}";
        States.Add(state);
        InitialState ??= state;
        return state;
    }

    /// <summary>
    /// Renames a state and every reference to it: the initial state, the state scope of rules and their state changes.
    /// False for a blank name or one that another state has.
    /// </summary>
    public bool RenameState(string state, string newName)
    {
        var name = newName.Trim();
        var index = States.IndexOf(state);
        if (index < 0 || name.Length == 0 || (name != state && States.Contains(name)))
        {
            return false;
        }

        if (name == state)
        {
            return true;
        }

        States[index] = name;
        if (InitialState == state)
        {
            InitialState = name;
        }

        foreach (var rule in Rules)
        {
            Rename(rule, state, name);
        }

        return true;
    }

    internal StateUsage GetStateUsage(string state) => new(
        Rules.FindAll(rule => rule.TargetStates.Contains(state)),
        Rules.FindAll(rule => rule.InState == state),
        Rules.FindAll(rule => (rule.InState == state || rule.InState is null) && rule.TargetStates.Any(target => target != state)));

    /// <summary>
    /// The rules with a condition, filter, argument or value that contains the state name as a word. A rename does not change
    /// such texts, which compare with the name as text, so they may need the new name.
    /// </summary>
    internal List<RuleDraft> FindRulesMentioning(string state)
    {
        var word = new Regex($@"(?<!\w){Regex.Escape(state)}(?!\w)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return Rules.FindAll(rule => rule.Texts.Any(word.IsMatch));
    }

    /// <summary>Removes a state that no rule uses; the first remaining state becomes initial when it was the initial state.</summary>
    public bool RemoveState(string state)
    {
        if (GetStateUsage(state).IsUsed || !States.Remove(state))
        {
            return false;
        }

        if (InitialState == state)
        {
            InitialState = States.Count > 0 ? States[0] : null;
        }

        return true;
    }

    private static void Rename(RuleDraft rule, string state, string name)
    {
        if (rule.InState == state)
        {
            rule.InState = name;
        }

        foreach (var action in rule.Actions)
        {
            if (action.Value is ChangeStateAction change && change.TargetState == state)
            {
                action.Value = change with { TargetState = name };
            }
        }
    }
}
