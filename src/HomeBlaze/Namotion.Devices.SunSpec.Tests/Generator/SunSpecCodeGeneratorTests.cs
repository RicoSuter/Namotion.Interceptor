using Namotion.Devices.SunSpec.Generator;

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
        Assert.Contains("SunSpecModel302.g.cs", Files.Keys);
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
    public void WhenGeneratingTwice_ThenOutputIsIdentical()
    {
        // Act
        var second = SunSpecCodeGenerator.Generate();

        // Assert
        Assert.Equal(Files, second);
    }
}
