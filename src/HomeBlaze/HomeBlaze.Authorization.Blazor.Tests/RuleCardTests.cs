using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using HomeBlaze.Components.Inputs;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>Tests of the rule cards and their chip fields, rendered in the role editor.</summary>
public sealed class RuleCardTests : RoleEditorTestContext
{
    [Fact]
    public async Task WhenAValidPatternIsEntered_ThenItIsAddedAsAChip()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", " /Devices/** ");

        // Assert
        Assert.Equal(["/Devices/**"], GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.Equal(["/Devices/**"], component.Instance.GetEditedRole().Allow[0].Paths.ToArray());
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenAPathIsPicked_ThenItsPatternGoesIntoTheFieldAndTheAddButtonAddsItOnceEdited()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var dialogProvider = Render<MudDialogProvider>();
        var component = RenderEditor(role);
        var field = "[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths'] input";

        // Act: the click waits for the dialog to close.
        var browse = component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths'] button[aria-label='Browse Paths']")
            .ClickAsync(new MouseEventArgs());
        var listsMembers = dialogProvider.FindComponent<SubjectPathPickerDialog>().Instance.ListsPropertiesWithoutState;
        await dialogProvider.Find("[data-testid='subject-path-picker'] input").InputAsync(new ChangeEventArgs { Value = "/Devices/Child" });
        await dialogProvider.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync(new MouseEventArgs());
        await browse;

        // Assert
        Assert.True(listsMembers);
        Assert.Equal("/Devices/Child/**", component.Find(field).GetAttribute("value"));
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-paths"));

        // Act
        await component.Find(field).InputAsync(new ChangeEventArgs { Value = "/Devices/Child/*" });
        await component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-add']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal(["/Devices/Child/*"], GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.Equal(["/Devices/Child/*"], component.Instance.GetEditedRole().Allow[0].Paths.ToArray());
        Assert.True(string.IsNullOrEmpty(component.Find(field).GetAttribute("value")));
    }

    [Fact]
    public async Task WhenTextIsTypedIntoAFieldWithSuggestionsAndAddIsClicked_ThenItIsAdded()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-types", "TestDevice");

        // Act
        await component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-types-add']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal(["TestDevice"], GetChips(GetCards(component, "allow")[0], "rule-types"));
    }

    [Fact]
    public async Task WhenThePathFieldIsEmptyOrInvalid_ThenItsAddButtonIsDisabled()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var add = "[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-add']";
        var isDisabledWhenEmpty = component.Find(add).HasAttribute("disabled");

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "Devices");
        var isDisabledWhenInvalid = component.Find(add).HasAttribute("disabled");
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices/**");

        // Assert
        Assert.True(isDisabledWhenEmpty);
        Assert.True(isDisabledWhenInvalid);
        Assert.False(component.Find(add).HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenAnInvalidPatternIsEntered_ThenItsErrorIsShownOnTheFieldAndItIsNotAdded()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "Devices");

        // Assert
        var field = component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths']");
        Assert.Contains("must start with '/'", field.TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public void WhenALoadedPatternIsInvalid_ThenItIsShownAsAnErrorChipAndTheRoleIsInvalid()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Paths = ["/Devices/**", "Garden"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-chip']");
        Assert.DoesNotContain("mud-chip-color-error", chips[0].ClassName);
        Assert.Contains("mud-chip-color-error", chips[1].ClassName);
        var tooltip = Assert.Single(component.FindComponent<RuleCard>().FindComponents<MudTooltip>());
        Assert.Equal("Path pattern 'Garden' must start with '/'.", tooltip.Instance.Text);
        Assert.False(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenATagIsNotAMemberTag_ThenItIsRejected()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-tags", "colour");

        // Assert
        Assert.Contains(
            "Unknown tag; use one of state, configuration, secret, query, operation, requires-confirmation, subjects, attribute, metadata.",
            component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-tags']").TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-tags"));
    }

    [Fact]
    public async Task WhenTheTypeFieldIsSearched_ThenItSuggestsTheKnownTypesAndTheTypedText()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var typeInput = component.FindComponents<ChipListInput>().Single(input => input.Instance.TestId == "rule-types");
        var search = typeInput.FindComponent<MudAutocomplete<string>>().Instance.SearchFunc!;

        // Act
        var suggestions = await search("o", CancellationToken.None)!;

        // Assert
        Assert.Equal(["o", nameof(HomeBlaze.Abstractions.IConfigurable), nameof(HomeBlaze.Abstractions.IIconProvider), nameof(HomeBlaze.Abstractions.ITitleProvider), "Role", "TestRoot"], suggestions);
    }

    [Fact]
    public async Task WhenATypeSuggestionIsPicked_ThenItIsAdded()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var typeInput = component.FindComponents<ChipListInput>().Single(input => input.Instance.TestId == "rule-types");
        var autocomplete = typeInput.FindComponent<MudAutocomplete<string>>();

        // Act
        await component.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync("TestRoot"));

        // Assert
        Assert.Equal(["TestRoot"], component.Instance.GetEditedRole().Allow[0].Types.ToArray());
    }

    [Fact]
    public async Task WhenValidTextIsTypedAndTheFieldLosesFocus_ThenItIsAddedAsAChip()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var selector = "[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths'] input";
        await component.Find(selector).InputAsync(new ChangeEventArgs { Value = " /Devices/** " });

        // Act
        await component.Find(selector).BlurAsync(new FocusEventArgs());

        // Assert
        Assert.Equal(["/Devices/**"], GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.Equal(["/Devices/**"], component.Instance.GetEditedRole().Allow[0].Paths.ToArray());
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenValidTextIsTypedButNotAdded_ThenSavingIncludesIt()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices/*");
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-types", "TestDevice");
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-members", "Temperature");

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(
            [new PermissionRule { Paths = ["/Devices/*"], Types = ["TestDevice"], Members = ["Temperature"], Actions = [PermissionAction.Read] }],
            role.Allow);
    }

    [Fact]
    public async Task WhenInvalidTextIsTypedButNotAdded_ThenTheRoleIsInvalidUntilItIsCleared()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var oldAllow = role.Allow;
        var component = RenderEditor(role);
        var validity = new List<bool>();
        component.Instance.IsValidChanged += validity.Add;

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "Devices");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.False(component.Instance.IsValid);
        Assert.Same(oldAllow, role.Allow);
        Assert.Single(component.FindAll("[data-testid='role-edit-tab-allow-error']"));

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "");

        // Assert
        Assert.True(component.Instance.IsValid);
        Assert.Equal([false, true], validity);
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-allow-error']"));
    }

    [Fact]
    public async Task WhenInvalidTextIsTypedIntoADenyRule_ThenOnlyTheDenyTabShowsTheErrorMarkerUntilItIsCleared()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Write] }];
        var component = RenderEditor(role);

        // Act
        await TypeTextAsync(component, "role-edit-deny-rule-0", "rule-paths", "Devices");

        // Assert
        Assert.False(component.Instance.IsValid);
        Assert.Single(component.FindAll("[data-testid='role-edit-tab-deny-error']"));
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-allow-error']"));

        // Act
        await TypeTextAsync(component, "role-edit-deny-rule-0", "rule-paths", "");

        // Assert
        Assert.True(component.Instance.IsValid);
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-deny-error']"));
    }

    [Fact]
    public void WhenEverySelectorOfARuleIsEmpty_ThenTheCardWarnsThatItAppliesToEveryMember()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow =
        [
            new PermissionRule { Actions = [PermissionAction.Read] },
            new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Write] }
        ];
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Write] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var allowWarning = GetCards(component, "allow")[0].QuerySelector("[data-testid='rule-any-member-warning']");
        Assert.Equal("This rule applies to every member.", allowWarning?.TextContent.Trim());
        Assert.Contains("mud-alert-text-error", allowWarning!.ClassName);
        Assert.Null(GetCards(component, "allow")[1].QuerySelector("[data-testid='rule-any-member-warning']"));

        var denyWarning = GetCards(component, "deny")[0].QuerySelector("[data-testid='rule-any-member-warning']");
        Assert.Contains("mud-alert-text-warning", denyWarning!.ClassName);
        Assert.All(component.FindComponents<RuleCard>().SelectMany(card => card.FindComponents<MudAlert>()),
            alert => Assert.True(string.IsNullOrEmpty(alert.Instance.Icon)));
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public void WhenADenyPathNamesAPropertyHoldingASubject_ThenTheCardHintsThatItMatchesOnlyThatMember()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Deny = [new PermissionRule { Paths = ["/Devices/Child", "/Devices/Temperature", "/Devices/*"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Equal(
            "'/Devices/Child' matches only that one member, not what is below it; add /** to deny everything below it as well.",
            component.Find("[data-testid='role-edit-deny-rule-0'] [data-testid='rule-single-member-hint']").TextContent.Trim());
    }

    [Theory]
    [InlineData("deny", "/Devices/Child/**")] // ends in a wildcard
    [InlineData("deny", "/Devices/Temperature")] // a value, below which is nothing
    [InlineData("deny", "/Devices")] // an entry, which the path warning covers
    [InlineData("deny", "/Devices/*/Child")] // an interior wildcard, matching several members
    [InlineData("deny", "/Devices/**/Child")] // an interior wildcard of any depth
    [InlineData("deny", "/Devices/Kitchn")] // a path that names nothing
    [InlineData("deny", "/Devices/Child/Start")] // a method
    [InlineData("allow", "/Devices/Child")] // an allow rule
    public void WhenAPathHasNothingBelowItLeftOut_ThenTheCardHasNoHint(string section, string path)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var rules = new List<PermissionRule> { new() { Paths = [path], Actions = [PermissionAction.Read] } };
        if (section == "deny")
        {
            role.Deny = rules;
        }
        else
        {
            role.Allow = rules;
        }

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Empty(component.FindAll("[data-testid='rule-single-member-hint']"));
    }

    [Fact]
    public async Task WhenATypedPathNamesAnEntry_ThenTheFieldWarnsAndTheAddedChipIsAWarningThatKeepsTheRoleValid()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices");
        var textWarning = component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-text-warning']").TextContent;
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices");

        // Assert
        Assert.StartsWith("Path pattern '/Devices' names a subject, not a member", textWarning);
        Assert.Empty(component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-text-warning']"));
        var chip = component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-chip']");
        Assert.Contains("mud-chip-color-warning", chip.ClassName);
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenATypedPathCoversTheEntry_ThenTheFieldDoesNotWarn()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices/**");

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-text-warning']"));
    }

    [Fact]
    public async Task WhenALoadedSelectorListsAValueTwice_ThenTheLaterOneIsAnErrorChipUntilItIsRemoved()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Members = ["TurnOn", "TurnOn"], Tags = ["state", "STATE"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-members-chip']");
        Assert.Equal(2, chips.Count);
        Assert.DoesNotContain("mud-chip-color-error", chips[0].ClassName);
        Assert.Contains("mud-chip-color-error", chips[1].ClassName);
        var tagChips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-tags-chip']");
        Assert.Contains("mud-chip-color-error", tagChips[1].ClassName);
        Assert.All(component.FindComponent<RuleCard>().FindComponents<MudTooltip>(), tooltip => Assert.Equal("Listed twice.", tooltip.Instance.Text));
        Assert.False(component.Instance.IsValid);

        // Act
        await RemoveChipAsync(component, "role-edit-allow-rule-0", "rule-members", 1);
        await RemoveChipAsync(component, "role-edit-allow-rule-0", "rule-tags", 1);

        // Assert
        Assert.Equal(["TurnOn"], component.Instance.GetEditedRole().Allow[0].Members.ToArray());
        Assert.Equal(["state"], component.Instance.GetEditedRole().Allow[0].Tags.ToArray());
        Assert.True(component.Instance.IsValid);
    }

    [Theory]
    [InlineData("rule-members", "Temperature, Set*,,Temperature", new[] { "Temperature", "Set*" })]
    [InlineData("rule-types", "ILight, ISwitch", new[] { "ILight", "ISwitch" })]
    [InlineData("rule-tags", "state, Secret", new[] { "state", "Secret" })]
    public async Task WhenTextWithCommasIsEntered_ThenEachPartIsAddedAsAChip(string input, string text, string[] expectedChips)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", input, text);

        // Assert
        Assert.Equal(expectedChips, GetChips(GetCards(component, "allow")[0], input));
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenTextWithCommasHasAnInvalidPart_ThenNothingIsAddedAndThePartsErrorIsShown()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-types", "ILight, I Switch");

        // Assert
        Assert.Contains("A type name contains no whitespace.",
            component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-types']").TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-types"));
        Assert.False(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenAPathWithACommaIsEntered_ThenItIsAddedAsOneChip()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", " /Devices/Living room, left/** ");

        // Assert
        Assert.Equal(["/Devices/Living room, left/**"], GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenALoadedPathHasAComma_ThenItIsValidAndSaved()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var rule = new PermissionRule { Paths = ["/Devices/Living room, left/**"], Actions = [PermissionAction.Read] };
        role.Allow = [rule];
        var component = RenderEditor(role);

        // Act: saving throws for an invalid role.
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        var chip = component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-chip']");
        Assert.DoesNotContain("mud-chip-color-error", chip.ClassName);
        Assert.Equal([rule], role.Allow);
    }

    [Theory]
    [InlineData("rule-members", "Turn On", "A member name contains no whitespace.")]
    [InlineData("rule-types", "I Light", "A type name contains no whitespace.")]
    [InlineData("rule-tags", "requires confirmation", "A tag contains no whitespace.")]
    public async Task WhenANameWithWhitespaceIsEntered_ThenItIsRejected(string input, string text, string expectedError)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", input, text);

        // Assert
        Assert.Contains(expectedError, component.Find($"[data-testid='role-edit-allow-rule-0'] [data-testid='{input}']").TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], input));
        Assert.False(component.Instance.IsValid);
    }

    [Theory]
    [InlineData("Members", " TurnOn", "Has leading or trailing whitespace.")]
    [InlineData("Members", "TurnOn, TurnOff", "Contains a comma, which separates values; remove it and add each part on its own.")]
    [InlineData("Members", "Turn On", "A member name contains no whitespace.")]
    [InlineData("Types", "ILight,ISwitch", "Contains a comma, which separates values; remove it and add each part on its own.")]
    [InlineData("Types", "I Light", "A type name contains no whitespace.")]
    [InlineData("Paths", "/Devices ", "Has leading or trailing whitespace.")]
    [InlineData("Tags", "state ", "Has leading or trailing whitespace.")]
    public void WhenALoadedValueWouldBeRejected_ThenItIsAnErrorChipAndTheRoleIsInvalid(string selector, string value, string expectedError)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var rule = new PermissionRule { Actions = [PermissionAction.Read] };
        role.Allow = [selector switch
        {
            "Members" => rule with { Members = [value] },
            "Types" => rule with { Types = [value] },
            "Paths" => rule with { Paths = [value] },
            _ => rule with { Tags = [value] }
        }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chip = component.Find($"[data-testid='role-edit-allow-rule-0'] [data-testid='rule-{selector.ToLowerInvariant()}-chip']");
        Assert.Contains("mud-chip-color-error", chip.ClassName);
        var tooltip = Assert.Single(component.FindComponent<RuleCard>().FindComponents<MudTooltip>());
        Assert.Equal(expectedError, tooltip.Instance.Text);
        Assert.False(component.Instance.IsValid);
    }

    [Fact]
    public void WhenATypeIsNotKnown_ThenItsChipWarnsWithoutMakingTheRoleInvalid()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["TestRoot", "HomeBlaze.Authorization.Role", "Testroot"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-types-chip']");
        Assert.DoesNotContain("mud-chip-color-warning", chips[0].ClassName);
        Assert.DoesNotContain("mud-chip-color-warning", chips[1].ClassName);
        Assert.Contains("mud-chip-color-warning", chips[2].ClassName);
        var tooltip = Assert.Single(component.FindComponent<RuleCard>().FindComponents<MudTooltip>());
        Assert.Equal("No known subject type or interface has this name; check its spelling and case.", tooltip.Instance.Text);
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public void WhenARuleCardIsShown_ThenTheTypesAndMembersSayTheyAreCaseSensitive()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var inputs = component.FindComponents<ChipListInput>();
        Assert.Contains("case-sensitive", inputs.Single(input => input.Instance.TestId == "rule-types").Instance.HelperText);
        Assert.Contains("case-sensitive", inputs.Single(input => input.Instance.TestId == "rule-members").Instance.HelperText);
        Assert.Equal("Set*", inputs.Single(input => input.Instance.TestId == "rule-members").Instance.Placeholder);
    }

    [Fact]
    public void WhenARuleCardIsShown_ThenThePathsFieldGivesMemberPathExamplesAndEachTabSaysOnceHowToAdd()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var inputs = component.FindComponents<ChipListInput>();
        var paths = inputs.Single(input => input.Instance.TestId == "rule-paths").Instance;
        Assert.Equal("/Devices/Sonos/*", paths.Placeholder);
        Assert.Contains("/Devices/**/Volume", paths.HelperText);
        Assert.All(inputs, input => Assert.DoesNotContain("click Add", input.Instance.HelperText));
        Assert.All(component.FindAll("[data-testid='role-edit-field-hint']"), hint => Assert.Equal(
            "In each field, press Enter, click Add or leave the field to add the typed text. Types, Members and Tags take several values separated by commas.",
            hint.TextContent.Trim()));
        Assert.Equal(2, component.FindAll("[data-testid='role-edit-field-hint']").Count);
    }

    [Fact]
    public void WhenRulesAreShown_ThenEachRemoveButtonNamesItsRule()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }, new PermissionRule { Actions = [PermissionAction.Write] }];
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Write] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Equal(
            ["Remove allow rule 1", "Remove allow rule 2", "Remove deny rule 1"],
            component.FindAll("[data-testid='rule-remove']").Select(button => button.GetAttribute("aria-label")));
    }

    [Fact]
    public async Task WhenALoadedRuleHasAnUnknownAction_ThenItIsAnErrorChipAndTheRoleIsInvalidUntilItIsRemoved()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read, new PermissionAction("run")] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var card = component.FindComponent<RuleCard>();
        var chip = Assert.Single(card.FindComponents<MudChip<string>>());
        Assert.Equal("run", chip.Find(".mud-chip-content").TextContent.Trim());
        Assert.Contains("mud-chip-color-error", chip.Find(".mud-chip").ClassName);
        Assert.Equal("Unknown action", Assert.Single(card.FindComponents<MudTooltip>()).Instance.Text);
        Assert.False(component.Instance.IsValid);

        // Act
        await chip.Find(".mud-chip-close-button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal([PermissionAction.Read], component.Instance.GetEditedRole().Allow[0].Actions.ToArray());
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public void WhenLoadedTagsAreInAnotherCase_ThenTheyAreValid()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Tags = ["STATE", "Secret"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-tags-chip']");
        Assert.All(chips, chip => Assert.DoesNotContain("mud-chip-color-error", chip.ClassName));
        Assert.True(component.Instance.IsValid);
    }
}
