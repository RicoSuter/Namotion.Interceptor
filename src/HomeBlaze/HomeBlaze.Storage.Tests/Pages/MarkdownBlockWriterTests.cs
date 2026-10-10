using System.Text.Json.Nodes;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownBlockWriterTests
{
    private const string Source =
        "before\n\n::::Demo.Card#main title=Old\nBody **text** {{ /A/B }}\n\n:::Demo.Motor speed=1\n```json\n{ \"cells\": [1] }\n```\n:::\n::::\n\nafter\n";

    private static MarkdownBlock Card => MarkdownPageParser.Parse(Source).Blocks[0];

    private static MarkdownBlock Motor => Card.Children[0];

    [Fact]
    public void WhenScalarsChange_ThenOnlyTheHeadLineChanges()
    {
        // Arrange
        var card = Card;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(
            Source, card, """{ "$type": "Demo.Card", "title": "New Title", "count": 2, "enabled": true, "nothing": null }""");

        // Assert
        Assert.Equal(
            Source.Replace("::::Demo.Card#main title=Old", "::::Demo.Card#main title=\"New Title\" count=2 enabled=true"),
            result);
    }

    [Fact]
    public void WhenComplexValueChanges_ThenTheJsonFenceIsReplaced()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, """{ "speed": 5, "cells": [3] }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Equal(":::Demo.Motor speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        Assert.Equal("{\"cells\":[3]}", motor.ConfigurationJson!.Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void WhenNoComplexValueIsLeft_ThenTheJsonFenceIsRemoved()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, """{ "speed": 5 }""");

        // Assert
        Assert.Contains(":::Demo.Motor speed=5\n:::\n::::", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void WhenBlockHadNoFence_ThenAComplexValueAddsOneAfterTheHead()
    {
        // Arrange
        var card = Card;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, card, """{ "title": "Old", "tags": ["a"] }""");

        // Assert
        Assert.Contains("::::Demo.Card#main title=Old\n```json\n{\n  \"tags\": [\n    \"a\"\n  ]\n}\n```\nBody **text**", result);
    }

    [Fact]
    public void WhenBlockIsCreated_ThenItIsAClosedThreeColonBlock()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Motor", "m1", """{ "$type": "Demo.Motor", "name": "My Motor", "speed": 3 }""");

        // Assert
        Assert.Equal(":::Demo.Motor#m1 name=\"My Motor\" speed=3\n:::", block);
    }

    [Fact]
    public void WhenCreatedBlockHasComplexValue_ThenItContainsAJsonFence()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Grid", null, """{ "rows": 1, "cells": [] }""");

        // Assert
        Assert.Equal(":::Demo.Grid rows=1\n```json\n{\n  \"cells\": []\n}\n```\n:::", block);
    }

    [Fact]
    public void WhenStringContainsLineBreak_ThenItGoesIntoTheJsonFence()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(
            Source, motorBlock, """{ "speed": 5, "description": "first\nsecond" }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Null(motor.Error);
        Assert.Equal(":::Demo.Motor speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        var configuration = JsonNode.Parse(motor.ConfigurationJson!)!.AsObject();
        Assert.Equal("first\nsecond", (string?)configuration["description"]);
    }

    [Fact]
    public void WhenKeyIsNotAValidHeadKey_ThenTheScalarGoesIntoTheJsonFence()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, """{ "speed": 5, "my-key": 7 }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Null(motor.Error);
        Assert.Equal(":::Demo.Motor speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        var configuration = JsonNode.Parse(motor.ConfigurationJson!)!.AsObject();
        Assert.Equal(7, (int?)configuration["my-key"]);
    }

    [Fact]
    public void WhenStringContainsQuoteAndBackslash_ThenItRoundTripsThroughTheHeadLine()
    {
        // Arrange
        var motorBlock = Motor;
        const string value = "say \"hi\" \\ bye";
        var configurationJson = new JsonObject { ["speed"] = 5, ["label"] = value }.ToJsonString();

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, configurationJson);

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Null(motor.Error);
        Assert.Equal(value, motor.Head!.Arguments.Single(argument => argument.Key == "label").Value);
    }
}
