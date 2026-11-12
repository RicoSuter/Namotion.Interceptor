using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Blazor.Editing;
using HomeBlaze.Automation.Blazor.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class AutomationDraftTests
{
    private static AutomationRule Rule(string name, string? inState, params AutomationAction[] actions) => new()
    {
        Name = name,
        Mode = AutomationRuleMode.On,
        InState = inState,
        Triggers = [new PropertyTrigger { Expression = "/Laundry/Plug/Power > 500" }],
        Actions = actions
    };

    private static ChangeStateAction Enter(string state) => new() { Id = "state-1", TargetState = state };

    private static Automation CreateWasher(IAutomationAuthorizer? authorizer = null) => TestAutomations.Create(automation =>
    {
        automation.Name = "Washer";
        automation.States = ["Idle", "Running"];
        automation.InitialState = "Idle";
        automation.RuleOrder = ["started"];
        automation.Rules = new Dictionary<string, AutomationRule>
        {
            ["finished"] = Rule("Finished", "Running", new InvokeOperationAction { Id = "call-1", OperationPath = "/Notify/Send" }, Enter("Idle")),
            ["started"] = Rule("Started", "Idle", Enter("Running"))
        };
    }, authorizer);

    [Fact]
    public void WhenARuleFileHoldsExplicitNulls_ThenTheDraftOpensValidatesAndSavesWithoutThem()
    {
        // Arrange
        const string json = """
            {
              "$type": "HomeBlaze.Automation.AutomationRule", "name": null, "mode": "On", "inState": null, "onlyIf": null,
              "triggers": [ null, { "$type": "PropertyTrigger", "expression": null, "rearm": null },
                            { "$type": "SubjectEventTrigger", "subjectPath": null, "eventType": null, "filter": null } ],
              "actions": [ null, { "$type": "InvokeOperation", "id": null, "operationPath": null, "arguments": null },
                           { "$type": "SetProperty", "id": null, "propertyPath": null, "value": null },
                           { "$type": "ChangeState", "id": null, "targetState": null } ]
            }
            """;
        var serializer = new ConfigurableSubjectSerializer(
            new TypeProvider().AddAssembly(typeof(AutomationRule).Assembly), new ServiceCollection().BuildServiceProvider());
        var rule = Assert.IsType<AutomationRule>(serializer.Deserialize(json));
        var automation = TestAutomations.Create(created => created.Rules = new Dictionary<string, AutomationRule> { ["k1"] = rule });

        // Act
        var draft = AutomationDraft.From(automation);
        var errors = draft.Validate();
        var expressions = new RuleExpressions(automation, "k1", draft.Rules[0].AllowsEvent);
        var inputErrors = draft.Rules[0].Actions.SelectMany(action => ActionInputs.Check(action.Value, automation, "k1", expressions)).ToList();
        draft.ApplyTo(automation);

        // Assert
        Assert.NotEmpty(errors);
        Assert.Empty(inputErrors);
        var saved = automation.Rules["k1"];
        Assert.All(saved.Actions, action => Assert.Equal(string.Empty, action.Id));
        Assert.Empty(Assert.IsType<InvokeOperationAction>(saved.Actions[0]).Arguments);
    }

    [Fact]
    public void WhenARuleIsAddedAfterAnotherWasRemoved_ThenItGetsANameNoOtherRuleHas()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());
        var third = draft.AddRule();
        draft.RemoveRule(draft.Rules[0]);
        draft.Rules[0].Name = "rule 4";

        // Act
        var added = draft.AddRule();

        // Assert
        Assert.Equal("Rule 3", third.Name);
        Assert.Equal("Rule 5", added.Name);
        Assert.DoesNotContain(draft.Validate(), error => error.Message.StartsWith("Another rule", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheDraftIsRead_ThenItHasTheConfigurationWithTheRulesInRunOrder()
    {
        // Act
        var draft = AutomationDraft.From(CreateWasher());

        // Assert
        Assert.Equal("Washer", draft.Name);
        Assert.Equal(["Idle", "Running"], draft.States.ToArray());
        Assert.Equal("Idle", draft.InitialState);
        Assert.Equal(["started", "finished"], draft.Rules.Select(rule => rule.Key).ToArray());
        Assert.Equal(2, draft.Rules[1].Actions.Count);
    }

    [Fact]
    public void WhenTheDraftIsApplied_ThenTheAutomationHasItsConfigurationAndOrder()
    {
        // Arrange
        var automation = CreateWasher();
        var draft = AutomationDraft.From(automation);
        draft.Name = " Washing machine ";
        draft.Rules[0].Name = "Start";
        var added = draft.AddRule();
        draft.MoveRule(added, -2);

        // Act
        draft.ApplyTo(automation);

        // Assert
        Assert.Equal("Washing machine", automation.Name);
        Assert.Equal([added.Key, "started", "finished"], automation.RuleOrder);
        Assert.Equal("Start", automation.Rules["started"].Name);
        Assert.Equal(AutomationRuleMode.DryRun, automation.Rules[added.Key].Mode);
        Assert.Equal("Idle", automation.InitialState);
    }

    [Fact]
    public void WhenTheRolesOfTheDraftAreChangedAndApplied_ThenTheAutomationHasThem()
    {
        // Arrange
        var automation = CreateWasher();
        automation.Roles = ["Reader"];
        var draft = AutomationDraft.From(automation);

        // Act
        var read = draft.Roles.ToArray();
        draft.Roles = ["Reader", "Operator"];
        draft.ApplyTo(automation);

        // Assert
        Assert.Equal(["Reader"], read);
        Assert.Equal(["Reader", "Operator"], automation.Roles);
    }

    [Fact]
    public void WhenTheDraftIsApplied_ThenTheRolesAreWrittenBeforeTheRules()
    {
        // Arrange
        var automation = CreateWasher();
        var recorder = new WriteRecorder(automation);
        ((IInterceptorSubject)automation).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry().WithParents());
        ((IInterceptorSubject)automation).Context.AddService<IWriteInterceptor>(recorder);
        var draft = AutomationDraft.From(automation);

        // Act
        draft.ApplyTo(automation);

        // Assert
        Assert.Contains(nameof(Automation.Roles), recorder.Written);
        Assert.True(recorder.Written.IndexOf(nameof(Automation.Roles)) < recorder.Written.IndexOf(nameof(Automation.Rules)), string.Join(", ", recorder.Written));
        Assert.Equal(nameof(Automation.Rules), recorder.Written[^1]);
    }

    private sealed class WriteRecorder(Automation automation) : IWriteInterceptor
    {
        public List<string> Written { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, automation))
            {
                Written.Add(context.Property.Name);
            }

            next(ref context);
        }
    }

    [Fact]
    public void WhenTheRolesCannotBeUsedAndARuleCallsAnOperation_ThenTheDraftHasTheRolesErrorWhichBlocksSave()
    {
        // Arrange
        var automation = TestAutomations.Create(
            authorizer: new UnusableRolesAuthorizer("the roles of the automation cannot be used: No roles are assigned."));
        automation.Rules = new Dictionary<string, AutomationRule>
        {
            ["call"] = Rule("Call", null, new InvokeOperationAction { Id = "call-1", OperationPath = "/Notify/Send" })
        };
        var draft = AutomationDraft.From(automation);

        // Act
        var errors = draft.Validate();

        // Assert
        Assert.Contains(errors, error => error.RuleKey is null && error.Message ==
            "The actions that call operations or write properties are refused: the roles of the automation cannot be used: No roles are assigned.");
    }

    [Fact]
    public void WhenAResolvedTargetIsWhatAnAutomationNeverWrites_ThenTheRuleHasTheSameErrorAsTheRuntime()
    {
        // Arrange
        var automation = TestAutomations.Create();
        ((IInterceptorSubject)automation).Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry().WithParents());
        automation.Rules = new Dictionary<string, AutomationRule>
        {
            ["rule"] = Rule("Rule", null, new SetPropertyAction { Id = "set-1", PropertyPath = "./Status", Value = "x" })
        };
        var draft = AutomationDraft.From(automation);

        // Act
        var errors = draft.Validate();

        // Assert
        Assert.Contains(errors, error => error.RuleKey == "rule" && error.Message == "action 'set-1': ./Status is read-only");
    }

    [Fact]
    public void WhenARuleIsAdded_ThenItHasANewKeyAnEmptyConditionAndRunsDry()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());

        // Act
        var rule = draft.AddRule();

        // Assert
        Assert.Matches("^[a-z][a-z0-9]{3}$", rule.Key);
        Assert.Equal("Rule 3", rule.Name);
        Assert.Equal(AutomationRuleMode.DryRun, rule.Mode);
        Assert.IsType<PropertyTrigger>(Assert.Single(rule.Triggers).Value);
    }

    [Fact]
    public void WhenANewRuleIsIncomplete_ThenTheDraftIsInvalidUntilItIsFilledIn()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());
        var rule = draft.AddRule();

        // Act
        var incomplete = draft.Validate();
        rule.Triggers[0].Value = new PropertyTrigger { Expression = "/Laundry/Plug/Power < 5" };
        rule.AddChangeState("Idle");
        var complete = draft.Validate();

        // Assert
        Assert.Contains(incomplete, error => error.RuleKey == rule.Key && error.Message == "it has no action");
        Assert.Empty(complete);
    }

    [Fact]
    public void WhenAStateIsRenamed_ThenTheInitialStateAndTheRulesFollow()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());

        // Act
        var isRenamed = draft.RenameState("Idle", " Standby ");

        // Assert
        Assert.True(isRenamed);
        Assert.Equal(["Standby", "Running"], draft.States.ToArray());
        Assert.Equal("Standby", draft.InitialState);
        Assert.Equal("Standby", draft.Rules[0].InState);
        Assert.Equal(["Standby"], draft.Rules[1].TargetStates.ToArray());
    }

    [Theory]
    [InlineData("Running")]
    [InlineData(" ")]
    public void WhenTheNewNameIsTakenOrBlank_ThenTheStateIsNotRenamed(string name)
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());

        // Act
        var isRenamed = draft.RenameState("Idle", name);

        // Assert
        Assert.False(isRenamed);
        Assert.Equal(["Idle", "Running"], draft.States.ToArray());
    }

    [Fact]
    public void WhenTextsMentionAStateAsAWord_ThenTheirRulesAreFound()
    {
        // Arrange
        var automation = TestAutomations.Create(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.Rules = new Dictionary<string, AutomationRule>
            {
                ["trigger"] = new() { Triggers = [new PropertyTrigger { Expression = "../CurrentState == Idle" }] },
                ["rearm"] = new() { Triggers = [new PropertyTrigger { Expression = "/a/B", Rearm = "/a/C == Idle" }] },
                ["filter"] = new() { Triggers = [new SubjectEventTrigger { Filter = "event/NewState == Idle" }] },
                ["onlyIf"] = new() { OnlyIf = "../CurrentState != Idle" },
                ["argument"] = new() { Actions = [new InvokeOperationAction { Arguments = new Dictionary<string, string> { ["Message"] = "Was Idle" } }] },
                ["value"] = new() { Actions = [new SetPropertyAction { Value = "Idle" }] },
                ["longer"] = new() { Triggers = [new PropertyTrigger { Expression = "../CurrentState == Idleness" }] },
                ["none"] = new() { Triggers = [new PropertyTrigger { Expression = "/a/B > 1" }] }
            };
        });
        var draft = AutomationDraft.From(automation);

        // Act
        var rules = draft.FindRulesMentioning("Idle");

        // Assert
        Assert.Equal(["argument", "filter", "onlyIf", "rearm", "trigger", "value"], rules.Select(rule => rule.Key).Order().ToArray());
    }

    [Fact]
    public void WhenAStateIsUsed_ThenItsUsageNamesTheRulesAndItCannotBeRemoved()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());

        // Act
        var usage = draft.GetStateUsage("Running");
        var isRemoved = draft.RemoveState("Running");

        // Assert
        Assert.Equal(["started"], usage.EnteredBy.Select(rule => rule.Key).ToArray());
        Assert.Equal(["finished"], usage.ActiveIn.Select(rule => rule.Key).ToArray());
        Assert.Equal(["finished"], usage.LeftBy.Select(rule => rule.Key).ToArray());
        Assert.Equal(2, usage.UseCount);
        Assert.False(isRemoved);
    }

    [Fact]
    public void WhenAnUnusedInitialStateIsRemoved_ThenTheFirstStateBecomesInitial()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());
        var added = draft.AddState();
        draft.InitialState = added;

        // Act
        var isRemoved = draft.RemoveState(added);

        // Assert
        Assert.Equal("State 3", added);
        Assert.True(isRemoved);
        Assert.Equal("Idle", draft.InitialState);
    }

    [Fact]
    public void WhenActionsAreAdded_ThenEachGetsTheNextFreeIdOfItsKind()
    {
        // Arrange
        var finished = AutomationDraft.From(CreateWasher()).Rules[1];

        // Act
        finished.AddCallOperation();
        finished.AddSetProperty();
        finished.AddChangeState("Running");

        // Assert
        Assert.Equal(["call-1", "state-1", "call-2", "set-1", "state-2"], finished.Actions.Select(action => action.Value.Id).ToArray());
    }

    [Fact]
    public void WhenAnEmptyRearmIsSaved_ThenTheRuleHasNoRearm()
    {
        // Arrange
        var rule = AutomationDraft.From(CreateWasher()).Rules[0];
        rule.Triggers[0].Value = new PropertyTrigger { Expression = "/Laundry/Plug/Power > 500", Rearm = " " };

        // Act
        var saved = rule.ToRule(_ => null);

        // Assert
        Assert.Null(Assert.IsType<PropertyTrigger>(Assert.Single(saved.Triggers)).Rearm);
    }

    [Fact]
    public void WhenADraftIsEdited_ThenTheSourceAutomationIsUntouchedUntilApplied()
    {
        // Arrange
        var arguments = new Dictionary<string, string> { ["notification.Message"] = "Done" };
        var automation = TestAutomations.Create(source =>
        {
            source.States = ["Idle", "Running"];
            source.InitialState = "Idle";
            source.RuleOrder = ["finished"];
            source.Rules = new Dictionary<string, AutomationRule>
            {
                ["finished"] = Rule("Finished", "Running", new InvokeOperationAction { Id = "call-1", OperationPath = "/Notify/Send", Arguments = arguments }, Enter("Idle"))
            };
        });
        var draft = AutomationDraft.From(automation);
        var rule = draft.Rules[0];

        // Act
        draft.RenameState("Running", "Busy");
        rule.Name = "Changed";
        rule.OnlyIf = "/a/B";
        var call = rule.Actions[0];
        call.Value = ((InvokeOperationAction)call.Value) with { OperationPath = "/Other/Send" };
        ((Dictionary<string, string>)((InvokeOperationAction)call.Value).Arguments)["notification.Message"] = "Edited";

        // Assert
        var source = automation.Rules["finished"];
        Assert.Equal(["Idle", "Running"], automation.States.ToArray());
        Assert.Equal("Finished", source.Name);
        Assert.Equal("Running", source.InState);
        Assert.Null(source.OnlyIf);
        var sourceCall = Assert.IsType<InvokeOperationAction>(source.Actions[0]);
        Assert.Equal("/Notify/Send", sourceCall.OperationPath);
        Assert.Equal("Done", sourceCall.Arguments["notification.Message"]);
        Assert.Equal("Done", arguments["notification.Message"]);
        Assert.Equal("Idle", Assert.IsType<ChangeStateAction>(source.Actions[1]).TargetState);
    }

    [Fact]
    public void WhenAPropertyWriteHasAPathButNoValue_ThenTheDraftNamesTheActionAndWithoutAPathNothingIsRequired()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher());
        var rule = draft.FindRule("finished")!;
        rule.AddSetProperty();
        var item = rule.Actions[^1];

        // Act
        var withoutPath = draft.Validate();
        item.Value = new SetPropertyAction { Id = item.Value.Id, PropertyPath = "/Demo/Motor/TargetSpeed" };
        var withPath = draft.Validate();

        // Assert
        Assert.DoesNotContain(withoutPath, error => error.Message.Contains("Required"));
        Assert.Contains(withPath, error => error.RuleKey == rule.Key && error.Message == "action 'set-1': Value: Required");
    }

    [Fact]
    public void WhenTheActionsAreSaved_ThenOnlyAnActionWithAChangedOperationLosesArgumentsItDoesNotTake()
    {
        // Arrange
        var rule = AutomationDraft.From(CreateWasher()).FindRule("finished")!;
        rule.AddCallOperation();
        rule.AddCallOperation();
        var arguments = new Dictionary<string, string> { ["speed"] = "5", ["gone"] = "x" };
        rule.Actions[^2].Value = new InvokeOperationAction { Id = "call-2", OperationPath = "/Demo/Motor/RunTest", Arguments = arguments };
        rule.Actions[^1].Value = new InvokeOperationAction { Id = "call-3", OperationPath = "/Demo/Motor/RunTest", Arguments = arguments };
        rule.Actions[^1].IsOperationChanged = true;
        var method = new MethodMetadata(_ => null) { Parameters = [new MethodParameter { Name = "speed", Type = typeof(int) }] };

        // Act
        var saved = rule.ToRule(path => path == "/Demo/Motor/RunTest" ? method : null);

        // Assert
        var calls = saved.Actions.OfType<InvokeOperationAction>().ToDictionary(action => action.Id);
        Assert.Equal(["gone", "speed"], calls["call-2"].Arguments.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["speed"], calls["call-3"].Arguments.Keys.ToArray());
    }

    [Fact]
    public void WhenThePermissionsOfTheDraftAreRequested_ThenTheyAreThoseOfItsUnsavedRoles()
    {
        // Arrange
        var authorizer = new RecordingAuthorizer();
        var draft = AutomationDraft.From(TestAutomations.Create(automation => automation.Roles = ["Reader"], authorizer));

        // Act
        draft.Roles = ["Operator"];
        var permissions = draft.GetPermissions();

        // Assert
        Assert.Equal("Operator", permissions.Error);
        Assert.Equal(["Operator"], authorizer.LastRoles!.ToArray());
    }

    [Fact]
    public void WhenTheDraftRolesCannotBeUsedAndARuleCallsAnOperation_ThenTheDraftIsInvalid()
    {
        // Arrange
        var draft = AutomationDraft.From(CreateWasher(new RecordingAuthorizer()));

        // Act
        draft.Roles = [];
        var errors = draft.Validate();

        // Assert
        Assert.Contains(errors, error => error.RuleKey is null && error.Message == "The actions that call operations or write properties are refused: ");
    }

    [Fact]
    public void WhenTheRolesOfTheDraftAreNull_ThenTheyAreAppliedAsNoRoles()
    {
        // Arrange
        var automation = CreateWasher();
        var draft = AutomationDraft.From(automation);

        // Act
        draft.Roles = null!;
        draft.ApplyTo(automation);

        // Assert
        Assert.Empty(automation.Roles);
    }

    // Answers every request with permissions whose error is the requested roles, so a test sees which roles were asked.
    private sealed class RecordingAuthorizer : IAutomationAuthorizer
    {
        public IReadOnlyList<string>? LastRoles { get; private set; }

        public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles)
        {
            LastRoles = roles;
            return new NamedPermissions(string.Join(", ", roles));
        }

        private sealed class NamedPermissions(string error) : IAutomationPermissions
        {
            public string? Error { get; } = error;

            public string? GetOperationDenial(RegisteredSubjectProperty operation) => Error;

            public string? GetPropertyWriteDenial(RegisteredSubjectProperty property) => Error;
        }
    }
}
