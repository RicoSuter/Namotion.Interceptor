extern alias SunSpecGenerator;

using SunSpecGenerator::Namotion.Devices.SunSpec.Definitions;
using SunSpecGenerator::Namotion.Devices.SunSpec.Generator;

namespace Namotion.Devices.SunSpec.Tests.Generator;

public class SunSpecCodeGeneratorTests
{
    private static readonly IReadOnlyDictionary<string, string> Files = SunSpecCodeGenerator.Generate();

    [Fact]
    public void WhenGenerating_ThenFamiliesShareOneClassAndTestModelsAreSkipped()
    {
        // Assert: 112 definitions, minus the two test models, minus the 10 family members merged into 4 classes, plus the factory.
        Assert.Equal(101, Files.Count);
        Assert.Contains("SunSpecInverter.g.cs", Files.Keys);
        Assert.Contains("SunSpecModel303.g.cs", Files.Keys);
        Assert.Contains("SunSpecDerAcMeasurement.g.cs", Files.Keys);
        Assert.DoesNotContain("SunSpecModel701.g.cs", Files.Keys);
        Assert.DoesNotContain("SunSpecModel101.g.cs", Files.Keys);
        Assert.DoesNotContain("SunSpecModel63001.g.cs", Files.Keys);
    }

    [Fact]
    public void WhenGeneratingStorageCapacity_ThenStateOfChargeIsAPercentage()
    {
        // Act
        var content = Files["SunSpecStorageCapacity.g.cs"];

        // Assert
        Assert.Contains("[ModbusRegister(4, ModbusDataType.U16, Scale = 0.01, ScaleFactorProperty = nameof(Pct_SF), NotAvailableValue = ModbusNotAvailableValue.UnsignedMaximum, Access = ModbusAccess.ReadOnly)]", content);
        Assert.Contains("[State(Title = \"State of Charge\", Unit = StateUnit.Percent)]", content);
        Assert.Contains("public partial decimal? SoC { get; internal set; }", content);
        Assert.Contains("public partial SunSpecStorageCapacitySta? Sta { get; internal set; }", content);
        Assert.Contains("public enum SunSpecStorageCapacitySta : ushort", content);
        Assert.Contains("public int ModelId => 713;", content);
    }

    [Fact]
    public void WhenGeneratingBattery_ThenStateOfChargeIsAPercentage()
    {
        // Act
        var content = Files["SunSpecBattery.g.cs"];

        // Assert
        Assert.Contains("[ModbusRegister(11, ModbusDataType.U16, Scale = 0.01, ScaleFactorProperty = nameof(SoC_SF), NotAvailableValue = ModbusNotAvailableValue.UnsignedMaximum, Access = ModbusAccess.ReadOnly)]", content);
        Assert.Contains("public partial decimal? SoC { get; internal set; }", content);
        Assert.Contains("public int ModelId => 802;", content);
    }

    [Fact]
    public void WhenGeneratingMppt_ThenModuleScaleFactorsComeFromTheModel()
    {
        // Act
        var content = Files["SunSpecMppt.g.cs"];

        // Assert
        Assert.Contains("public partial SunSpecMpptModuleGroup[] Module { get; internal set; }", content);
        Assert.Contains("public partial class SunSpecMpptModuleGroup : IModbusBaseAddressProvider, ITitleProvider, IModbusScaleFactorProvider", content);
        Assert.Contains("nameof(DCA) => new PropertyReference(Parent, nameof(SunSpecMppt.DCA_SF)),", content);
        Assert.Contains("Module = SunSpecGroups.Update(Module, instance.GetGroup(\"module\"), group => new SunSpecMpptModuleGroup(this, group.Address, group.Index));", content);
    }

    [Fact]
    public void WhenGeneratingNestedGroups_ThenScaleFactorsComeFromTheModel()
    {
        // Act
        var content = Files["SunSpecModel705.g.cs"];

        // Assert
        Assert.Contains("public partial class SunSpecModel705CrvPtGroup", content);
        Assert.Contains("new PropertyReference(Parent.Parent, nameof(SunSpecModel705.V_SF))", content);
    }

    [Fact]
    public void WhenGeneratingTheFactory_ThenFamilyMembersCreateTheirClass()
    {
        // Act
        var content = Files["SunSpecModelFactory.g.cs"];

        // Assert
        Assert.Contains("101 or 102 or 103 => new SunSpecInverter(modelId, baseAddress, length),", content);
        Assert.Contains("713 => new SunSpecStorageCapacity(baseAddress, length),", content);
    }

    [Fact]
    public void WhenGeneratingAFamily_ThenTheTitleIsTheLabelOfTheModel()
    {
        // Act
        var content = Files["SunSpecInverter.g.cs"];

        // Assert
        Assert.Contains(
            "    public string Title => ModelId switch\n" +
            "    {\n" +
            "        101 => \"Inverter (Single Phase)\",\n" +
            "        102 => \"Inverter (Split-Phase)\",\n" +
            "        _ => \"Inverter (Three Phase)\"\n" +
            "    };\n", content);
        Assert.Contains("/// SunSpec models 101 (Inverter (Single Phase)), 102 (Inverter (Split-Phase)), 103 (Inverter (Three Phase)).", content);
    }

    [Fact]
    public void WhenGeneratingTwice_ThenOutputIsIdentical()
    {
        // Act
        var second = SunSpecCodeGenerator.Generate();

        // Assert
        Assert.Equal(Files, second);
    }

    [Fact]
    public void WhenFamilyMembersDifferOnlyInPointNames_ThenTheyShareOneClass()
    {
        // Arrange
        var definitions = new[] { CreateModel(64000, Point("W", units: "W")), CreateModel(64001, Point("Watts", units: "W")) };

        // Act
        var files = SunSpecCodeGenerator.Generate(definitions, CreateFamilyOverrides(64000, 64001));

        // Assert
        Assert.Equal(["SunSpecModelFactory.g.cs", "SunSpecTest.g.cs"], files.Keys);
    }

    [Fact]
    public void WhenFamilyMembersDifferInAPointUnit_ThenGenerationFails()
    {
        // Arrange
        var definitions = new[] { CreateModel(64000, Point("W", units: "W")), CreateModel(64001, Point("W", units: "VA")) };

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecCodeGenerator.Generate(definitions, CreateFamilyOverrides(64000, 64001)));
        Assert.Contains("Point W", exception.Message);
        Assert.Contains("model 64001 and its class representative 64000", exception.Message);
    }

    [Fact]
    public void WhenAFamilyMemberLeavesOutSymbols_ThenItSharesTheClass()
    {
        // Arrange
        var definitions = new[]
        {
            CreateModel(64000, Point("Evt", type: "bitfield16", symbols: [Symbol("Failure", 0), Symbol("Reserved", 1)])),
            CreateModel(64001, Point("Evt", type: "bitfield16", symbols: [Symbol("Failure", 0)]))
        };

        // Act
        var files = SunSpecCodeGenerator.Generate(definitions, CreateFamilyOverrides(64000, 64001));

        // Assert
        Assert.Contains("Reserved = 0x2,", files["SunSpecTest.g.cs"]);
    }

    [Fact]
    public void WhenAFamilyMemberHasASymbolValueTheRepresentativeLacks_ThenGenerationFails()
    {
        // Arrange
        var definitions = new[]
        {
            CreateModel(64000, Point("St", type: "enum16", symbols: [Symbol("ON", 1)])),
            CreateModel(64001, Point("St", type: "enum16", symbols: [Symbol("ON", 2)]))
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecCodeGenerator.Generate(definitions, CreateFamilyOverrides(64000, 64001)));
        Assert.Contains("Point St", exception.Message);
        Assert.Contains("the representative lacks the symbol value 2", exception.Message);
    }

    [Fact]
    public void WhenAPointIsNamedLikeAKeyword_ThenGenerationFails()
    {
        // Arrange
        var definitions = new[] { CreateModel(64000, Point("class")) };

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecCodeGenerator.Generate(definitions, new SunSpecGeneratorOverrides()));
        Assert.Contains("'class' is a C# keyword", exception.Message);
    }

    [Fact]
    public void WhenAModelDoesNotStartWithIdAndLength_ThenGenerationFails()
    {
        // Arrange
        var definition = new SunSpecModelDefinition { Id = 64000, Group = new SunSpecGroupDefinition { Name = "test", Points = [Point("ID"), Point("W")] } };

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecCodeGenerator.Generate([definition], new SunSpecGeneratorOverrides()));
        Assert.Contains("Model 64000 does not start with the ID and L points", exception.Message);
    }

    [Fact]
    public void WhenEnumMemberNamesStillCollideAfterAppendingTheValue_ThenGenerationFails()
    {
        // Arrange: "A" (2) falls back to "A_2", which the first symbol already uses.
        var definitions = new[] { CreateModel(64000, Point("St", type: "enum16", symbols: [Symbol("A_2", 0), Symbol("A", 1), Symbol("A", 2)])) };

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecCodeGenerator.Generate(definitions, new SunSpecGeneratorOverrides()));
        Assert.Contains("Symbol A of SunSpecModel64000St collides", exception.Message);
    }

    [Theory]
    [InlineData(new[] { 64000, 64001 }, null, "lists several models but no representative")]
    [InlineData(new[] { 64000, 64001 }, 64002, "The representative 64002 of class SunSpecTest")]
    [InlineData(new[] { 64000, 64002 }, 64000, "lists model 64002, which has no definition")]
    [InlineData(new[] { 64000, 64003 }, 64000, "lists model 64003, which is excluded")]
    public void WhenOverridesAreInvalid_ThenGenerationFailsNamingTheProblem(int[] models, int? representative, string expectedMessage)
    {
        // Arrange
        var definitions = new[] { CreateModel(64000), CreateModel(64001), CreateModel(64003) };
        var overrides = new SunSpecGeneratorOverrides
        {
            ExcludedModels = [64003],
            Classes = [new SunSpecClassOverride { Name = "SunSpecTest", Models = models, Representative = representative }]
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecCodeGenerator.Generate(definitions, overrides));
        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void WhenTextContainsControlCharacters_ThenTheLiteralEscapesThem()
    {
        // Act
        var literal = CSharpText.Literal("a\r\n\tb\"\\\u0001\u2028");

        // Assert
        Assert.Equal("\"a\\r\\n\\tb\\\"\\\\\\u0001\\u2028\"", literal);
    }

    private static SunSpecGeneratorOverrides CreateFamilyOverrides(int representative, int member) => new()
    {
        Classes = [new SunSpecClassOverride { Name = "SunSpecTest", Models = [representative, member], Representative = representative }]
    };

    private static SunSpecModelDefinition CreateModel(int modelId, params SunSpecPointDefinition[] points) => new()
    {
        Id = modelId,
        Group = new SunSpecGroupDefinition { Name = "test", Points = [Point("ID"), Point("L"), .. points] }
    };

    private static SunSpecPointDefinition Point(string name, string type = "uint16", string? units = null, SunSpecSymbolDefinition[]? symbols = null) => new()
    {
        Name = name,
        Type = type,
        Size = 1,
        Units = units,
        Symbols = symbols ?? []
    };

    private static SunSpecSymbolDefinition Symbol(string name, long value) => new() { Name = name, Value = value };
}
