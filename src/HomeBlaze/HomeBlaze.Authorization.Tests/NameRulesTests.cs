using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class NameRulesTests
{
    private const string InvalidRoleNameError =
        "The role name is not valid. Use lowercase letters a-z, digits, '.', '-' or '_', starting with a letter or digit, at most 64 characters.";

    [Theory]
    [InlineData("a")]
    [InlineData("7")]
    [InlineData("family")]
    [InlineData("kitchen.lights")]
    [InlineData("kids-room_2")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void WhenANameFollowsTheRule_ThenNoErrorIsReturned(string name)
    {
        // Act
        var error = NameRules.GetNameError(name, "role name");

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Family")]
    [InlineData("FAMILY")]
    [InlineData("family room")]
    [InlineData(" family")]
    [InlineData("family ")]
    [InlineData(".family")]
    [InlineData("-family")]
    [InlineData("_family")]
    [InlineData("café")]
    [InlineData("fam​ily")]
    [InlineData("fam‮ily")]
    [InlineData("fam/ily")]
    [InlineData("fam[0]")]
    [InlineData("ｆamily")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public void WhenANameBreaksTheRule_ThenItIsRefusedWithTheRule(string name)
    {
        // Act
        var error = NameRules.GetNameError(name, "role name");

        // Assert
        Assert.Equal(InvalidRoleNameError, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WhenANameIsMissing_ThenItIsRequired(string? name)
    {
        // Act
        var error = NameRules.GetNameError(name, "user name");

        // Assert
        Assert.Equal("A user name is required.", error);
    }

    [Fact]
    public void WhenARoleNameBreaksTheRule_ThenTheRoleNameErrorNamesTheRule()
    {
        // Act
        var error = AuthorizationService.GetRoleNameError("Family");

        // Assert
        Assert.Equal(InvalidRoleNameError, error);
    }

    [Fact]
    public void WhenTheBuiltInNamesAreChecked_ThenEachFollowsTheRule()
    {
        // Arrange
        string[] names = [NameRules.AnonymousUserName, NameRules.McpUserName, BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName];

        // Act & Assert
        Assert.Equal(["anonymous", "mcp", "reader", "operator", "everything"], names);
        Assert.All(names, name => Assert.Null(NameRules.GetNameError(name, "name")));
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("mcp")]
    public void WhenAUserNameIsABuiltInName_ThenItIsBuiltIn(string name)
    {
        // Act & Assert
        Assert.True(NameRules.IsBuiltInUserName(name));
    }

    [Theory]
    [InlineData("mom")]
    [InlineData("MCP")]
    [InlineData("Anonymous")]
    public void WhenAUserNameIsNotExactlyABuiltInName_ThenItIsNotBuiltIn(string name)
    {
        // Act & Assert
        Assert.False(NameRules.IsBuiltInUserName(name));
    }

    [Theory]
    [InlineData("Everything")]
    [InlineData("READER")]
    public void WhenARoleNameDiffersFromABuiltInRoleInCase_ThenItIsNotBuiltIn(string name)
    {
        // Act & Assert
        Assert.False(BuiltInRoles.IsBuiltIn(name));
    }

    [Theory]
    [InlineData("Rico Suter", "Rico Suter")]
    [InlineData("  J\u00fcrgen M\u00fcller  ", "J\u00fcrgen M\u00fcller")]
    [InlineData("\u0631\u0636\u0627 \u0627\u062D\u0645\u062F\u06CC", "\u0631\u0636\u0627 \u0627\u062D\u0645\u062F\u06CC")]
    [InlineData("\u5C71\u7530 \u592A\u90CE", "\u5C71\u7530 \u592A\u90CE")]
    [InlineData("Zo\u00EB (Kitchen) \U0001F3E0", "Zo\u00EB (Kitchen) \U0001F3E0")]
    [InlineData("\uD64D\uAE38\uB3D9", "\uD64D\uAE38\uB3D9")]
    [InlineData("\u0905\u0930\u094D\u091C\u0941\u0928", "\u0905\u0930\u094D\u091C\u0941\u0928")]
    [InlineData("Nguye\u0302\u0303n", "Nguye\u0302\u0303n")]
    [InlineData("Ze\u0301\u0302\u0303\u0304", "Ze\u0301\u0302\u0303\u0304")]
    public void WhenADisplayNameIsValid_ThenItIsTrimmedAndHasNoError(string displayName, string expected)
    {
        // Act
        var normalized = NameRules.NormalizeDisplayName(displayName);
        var error = NameRules.GetDisplayNameError(normalized);

        // Assert
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenADisplayNameIsEmpty_ThenItIsNone(string? displayName)
    {
        // Act
        var normalized = NameRules.NormalizeDisplayName(displayName);

        // Assert
        Assert.Null(normalized);
        Assert.Null(NameRules.GetDisplayNameError(normalized));
    }

    [Theory]
    [InlineData("Rico\u202ESuter")] // right-to-left override
    [InlineData("Rico\u2066Suter")] // left-to-right isolate
    [InlineData("Rico\u200BSuter")] // zero-width space
    [InlineData("Rico\u200DSuter")] // zero-width joiner
    [InlineData("Rico\uFEFFSuter")] // zero-width no-break space
    [InlineData("Rico\u0007Suter")] // control character
    [InlineData("Rico\nSuter")]
    [InlineData("Rico\u2028Suter")] // line separator
    [InlineData("Rico\u2029Suter")] // paragraph separator
    [InlineData("Rico\uE000Suter")] // private use
    [InlineData("Rico\U000F0000Suter")] // supplementary private use
    [InlineData("Rico\u0378Suter")] // unassigned
    [InlineData("Rico\uFFFFSuter")] // noncharacter
    public void WhenADisplayNameHasAnInvisibleControlOrUnassignedCharacter_ThenItIsRefused(string displayName)
    {
        // Act
        var error = NameRules.GetDisplayNameError(NameRules.NormalizeDisplayName(displayName));

        // Assert
        Assert.Equal("The display name contains a control, invisible, private use or unassigned character.", error);
    }

    [Theory]
    [InlineData("\u3164")] // Hangul filler
    [InlineData("\u2800\u2800")] // Braille blank
    [InlineData("\u115F\u1160")] // Hangul choseong and jungseong fillers
    [InlineData("\uFFA0")] // halfwidth Hangul filler
    [InlineData("\u0301")] // a mark alone
    [InlineData("\u3164 \u0301")]
    public void WhenADisplayNameHasNoVisibleCharacter_ThenItIsRefused(string displayName)
    {
        // Act
        var error = NameRules.GetDisplayNameError(NameRules.NormalizeDisplayName(displayName));

        // Assert
        Assert.Equal("The display name has no visible character.", error);
    }

    [Fact]
    public void WhenADisplayNameStacksMoreThanFourMarksOnACharacter_ThenItIsRefused()
    {
        // Act
        var error = NameRules.GetDisplayNameError("Ze\u0301\u0302\u0303\u0304\u0308");

        // Assert
        Assert.Equal("The display name stacks more than 4 marks on a character.", error);
    }

    [Fact]
    public void WhenADisplayNameHasALoneSurrogate_ThenItIsRefused()
    {
        // Arrange
        // Built at run time: test discovery serializes InlineData as UTF-8, which would turn the surrogate into U+FFFD.
        var displayName = "Rico" + '\uD800' + "Suter";

        // Act
        var error = NameRules.GetDisplayNameError(displayName);

        // Assert
        Assert.Equal("The display name contains a control, invisible, private use or unassigned character.", error);
    }

    [Fact]
    public void WhenADisplayNameIsLongerThanTheMaximum_ThenItIsRefused()
    {
        // Arrange
        var longest = new string('a', NameRules.MaximumDisplayNameLength);

        // Act
        var longestError = NameRules.GetDisplayNameError(longest);
        var tooLongError = NameRules.GetDisplayNameError(longest + "a");

        // Assert
        Assert.Equal(100, NameRules.MaximumDisplayNameLength);
        Assert.Null(longestError);
        Assert.Equal("A display name has at most 100 characters.", tooLongError);
    }
}
