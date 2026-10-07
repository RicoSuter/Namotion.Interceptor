using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Discovery;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Models;
using Namotion.Devices.SunSpec.Tests.Testing;

namespace Namotion.Devices.SunSpec.Tests.Discovery;

public class SunSpecUnitBuilderTests
{
    private static async Task<SunSpecLogicalDevice[]> BuildAsync(SunSpecTestChain chain, SunSpecLogicalDevice[]? current = null, SunSpecModelCatalog? catalog = null)
    {
        var result = await InMemoryRegisters.ReadAsync(chain);
        return SunSpecUnitBuilder.Build(1, result!, current ?? [], catalog ?? SunSpecModelCatalog.Empty);
    }

    private static SunSpecTestChain CreateSolarEdgeChain() => new SunSpecTestChain()
        .AddModel(1, length: 65).AddModel(103)
        .AddModel(1, length: 65).AddModel(203)
        .AddModel(1, length: 65).AddModel(203);

    [Fact]
    public async Task WhenMetersHaveTheirOwnCommonModel_ThenEachBecomesALogicalDevice()
    {
        // Act
        var devices = await BuildAsync(CreateSolarEdgeChain());

        // Assert
        Assert.Equal(3, devices.Length);
        Assert.Equal(40002, devices[0].Common!.BaseAddress);
        Assert.Equal(40069, Assert.IsType<SunSpecInverter>(Assert.Single(devices[0].Models)).BaseAddress);
        Assert.Equal(40121, devices[1].Common!.BaseAddress);
        Assert.Equal(40188, Assert.IsType<SunSpecAcMeter>(Assert.Single(devices[1].Models)).BaseAddress);
        Assert.Equal(40295, devices[2].Common!.BaseAddress);
        Assert.Equal(2, devices[2].Index);
        Assert.All(devices, device => Assert.Equal(1, device.UnitId));
    }

    [Fact]
    public async Task WhenChainDoesNotStartWithCommon_ThenLeadingModelsHaveNoCommon()
    {
        // Act
        var devices = await BuildAsync(new SunSpecTestChain().AddModel(103).AddModel(1).AddModel(203));

        // Assert
        Assert.Equal(2, devices.Length);
        Assert.Null(devices[0].Common);
        Assert.IsType<SunSpecInverter>(Assert.Single(devices[0].Models));
        Assert.NotNull(devices[1].Common);
        Assert.IsType<SunSpecAcMeter>(Assert.Single(devices[1].Models));
    }

    [Fact]
    public async Task WhenChainIsEmpty_ThenThereAreNoDevices()
    {
        // Act
        var devices = await BuildAsync(new SunSpecTestChain());

        // Assert
        Assert.Empty(devices);
    }

    [Fact]
    public async Task WhenChainIsUnchanged_ThenAllSubjectsAreKept()
    {
        // Arrange
        var first = await BuildAsync(CreateSolarEdgeChain());
        var firstModels = first[0].Models;

        // Act
        var second = await BuildAsync(CreateSolarEdgeChain(), first);

        // Assert
        Assert.Same(first, second);
        Assert.Same(firstModels, second[0].Models);
        Assert.Same(first[0].Models[0], second[0].Models[0]);
    }

    [Fact]
    public async Task WhenAModelChangesAtAnAddress_ThenOnlyThatSubjectIsReplaced()
    {
        // Arrange
        var first = await BuildAsync(new SunSpecTestChain().AddModel(1, length: 65).AddModel(103));
        var common = first[0].Common;

        // Act
        var second = await BuildAsync(new SunSpecTestChain().AddModel(1, length: 65).AddModel(101), first);

        // Assert
        Assert.Same(first, second);
        Assert.Same(common, second[0].Common);
        Assert.Equal(101, Assert.IsType<SunSpecInverter>(second[0].Models[0]).ModelId);
    }

    [Fact]
    public async Task WhenAModelLengthChanges_ThenTheSubjectIsReplaced()
    {
        // Arrange
        var first = await BuildAsync(new SunSpecTestChain().AddModel(1, length: 65));
        var common = first[0].Common;

        // Act
        var second = await BuildAsync(new SunSpecTestChain().AddModel(1), first);

        // Assert
        Assert.Same(first, second);
        Assert.NotSame(common, second[0].Common);
        Assert.Equal(66, second[0].Common!.Length);
    }

    [Fact]
    public async Task WhenALogicalDeviceIsRemoved_ThenANewArrayKeepsTheRemainingDevices()
    {
        // Arrange
        var first = await BuildAsync(CreateSolarEdgeChain());

        // Act
        var second = await BuildAsync(new SunSpecTestChain().AddModel(1, length: 65).AddModel(103).AddModel(1, length: 65).AddModel(203), first);

        // Assert
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Length);
        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
        Assert.Same(first[1].Models[0], second[1].Models[0]);
    }

    [Fact]
    public async Task WhenModelHasNoClass_ThenDynamicOrUnknownModelIsCreated()
    {
        // Arrange
        var definition = SunSpecDynamicModelTests.ParseDefinition();
        var catalog = new SunSpecModelCatalog(new Dictionary<int, SunSpecModelDefinition> { [64999] = definition });
        var chain = new SunSpecTestChain()
            .AddModel(1)
            .AddModel(64999, new Dictionary<string, object?> { ["N"] = 0 }, definition: definition)
            .AddRawModel([64998, 2, 0, 0]);

        // Act
        var devices = await BuildAsync(chain, catalog: catalog);

        // Assert
        Assert.Equal(64999, Assert.IsType<SunSpecDynamicModel>(devices[0].Models[0]).ModelId);
        Assert.Equal(64998, Assert.IsType<SunSpecUnknownModel>(devices[0].Models[1]).ModelId);
    }

    [Fact]
    public async Task WhenADefinitionIsAddedForAnUnknownModel_ThenADynamicModelReplacesIt()
    {
        // Arrange
        var definition = SunSpecDynamicModelTests.ParseDefinition();
        var chain = new SunSpecTestChain().AddModel(1).AddModel(64999, new Dictionary<string, object?> { ["N"] = 0 }, definition: definition);
        var first = await BuildAsync(chain);
        var unknownModel = Assert.IsType<SunSpecUnknownModel>(first[0].Models[0]);
        var catalog = new SunSpecModelCatalog(new Dictionary<int, SunSpecModelDefinition> { [64999] = definition });

        // Act
        var second = await BuildAsync(chain, first, catalog);

        // Assert
        var dynamicModel = Assert.IsType<SunSpecDynamicModel>(second[0].Models[0]);
        Assert.Equal(unknownModel.BaseAddress, dynamicModel.BaseAddress);
    }

    [Fact]
    public async Task WhenModelHasRepeatingGroups_ThenGroupSubjectsAreCreated()
    {
        // Arrange
        var modules = new[]
        {
            new Dictionary<string, object?> { ["DCA"] = 1234 },
            new Dictionary<string, object?> { ["DCA"] = 567 }
        };
        var chain = new SunSpecTestChain().AddModel(1).AddModel(160, new Dictionary<string, object?> { ["N"] = 2, ["module"] = modules });

        // Act
        var devices = await BuildAsync(chain);

        // Assert
        var mppt = Assert.IsType<SunSpecMppt>(devices[0].Models[0]);
        Assert.Equal(2, mppt.Module.Length);
        Assert.Same(mppt, mppt.Module[1].Parent);
    }

    [Fact]
    public async Task WhenGroupsDoNotFitTheModel_ThenInvalidDataExceptionIsThrown()
    {
        // Arrange
        var definition = SunSpecDynamicModelTests.ParseDefinition();
        var catalog = new SunSpecModelCatalog(new Dictionary<int, SunSpecModelDefinition> { [64999] = definition });
        var chain = new SunSpecTestChain().AddModel(1).AddModel(64999, new Dictionary<string, object?> { ["N"] = 5 }, definition: definition);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => BuildAsync(chain, catalog: catalog));
    }
}
