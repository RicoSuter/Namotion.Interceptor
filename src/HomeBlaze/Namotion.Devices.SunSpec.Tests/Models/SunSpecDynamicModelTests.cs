using System.Reactive.Concurrency;
using System.Text;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Models;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.SunSpec.Tests.Models;

public class SunSpecDynamicModelTests
{
    private const int ModelAddress = 40100;

    internal const string DefinitionJson = """
        { "id": 64999, "group": { "name": "vendor", "label": "Vendor Block", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
            { "name": "W", "type": "int16", "size": 1, "units": "W", "sf": "W_SF", "label": "Power" },
            { "name": "W_SF", "type": "sunssf", "size": 1 }, { "name": "N", "type": "count", "size": 1 },
            { "name": "A_SF", "type": "sunssf", "size": 1 } ],
          "groups": [ { "name": "channel", "count": "N", "points": [
            { "name": "A", "type": "uint16", "size": 1, "units": "A", "sf": "A_SF" } ] } ] } }
        """;

    // Registers: ID, L, V_SF, Tms (2), St, settings (Hz_SF, Hz), string[0] (Id), string[0].cell[0] (V), string[0].cell[1] (V).
    private const string NestedDefinitionJson = """
        { "id": 64998, "group": { "name": "nested", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
            { "name": "V_SF", "type": "sunssf", "size": 1 },
            { "name": "Tms", "type": "uint32", "size": 2, "units": "Secs" },
            { "name": "St", "type": "enum16", "size": 1, "symbols": [ { "name": "OFF", "value": 1 }, { "name": "ON", "value": 2 } ] } ],
          "groups": [
            { "name": "settings", "points": [
              { "name": "Hz_SF", "type": "sunssf", "size": 1 }, { "name": "Hz", "type": "uint16", "size": 1, "units": "Hz", "sf": "Hz_SF" } ] },
            { "name": "string", "count": 1, "points": [ { "name": "Id", "type": "uint16", "size": 1 } ],
              "groups": [ { "name": "cell", "count": 2, "points": [
                { "name": "V", "type": "uint16", "size": 1, "units": "V", "sf": "V_SF" } ] } ] } ] } }
        """;

    internal static SunSpecModelDefinition ParseDefinition() => Parse(DefinitionJson);

    private static SunSpecModelDefinition Parse(string json) => SunSpecDefinitions.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static SunSpecDynamicModel CreateAttachedModel(string json, ushort[]? registers)
    {
        var definition = Parse(json);
        var model = new SunSpecDynamicModel(definition, ModelAddress, registers is null ? 0 : registers.Length - 2);
        if (registers is not null)
        {
            ((ISunSpecGroupOwner)model).UpdateGroups(SunSpecLayout.Resolve(definition, ModelAddress, registers));
        }

        TestRoot.Attach(model);
        model.EnsureProperties();
        return model;
    }

    private static SunSpecDynamicModel CreateAttachedModel() => CreateAttachedModel(DefinitionJson, [64999, 6, 1500, 0, 2, 0xFFFF, 10, 20]);

    private static SunSpecDynamicModel CreateNestedModel() => CreateAttachedModel(NestedDefinitionJson, [64998, 9, 0xFFFF, 0, 30, 1, 0, 500, 7, 230, 231]);

    private static void ApplyLayout(SunSpecDynamicModel model, ushort[] registers)
    {
        ((ISunSpecGroupOwner)model).UpdateGroups(SunSpecLayout.Resolve(model.GetDefinition(), ModelAddress, registers));
        model.EnsureProperties();
    }

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string name)
        => subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    private static SunSpecDynamicGroup[] GetChannels(SunSpecDynamicModel model)
        => (SunSpecDynamicGroup[])GetProperty(model, "Channel").GetValue()!;

    [Fact]
    public void WhenAttached_ThenPointsBecomeRegisterProperties()
    {
        // Act
        var model = CreateAttachedModel();

        // Assert
        var power = GetProperty(model, "W");
        var attribute = Assert.Single(power.ReflectionAttributes.OfType<ModbusRegisterAttribute>());
        Assert.Equal(2, attribute.Address);
        Assert.Equal("W_SF", attribute.ScaleFactorProperty);
        Assert.Equal(typeof(decimal?), power.Type);
    }

    [Fact]
    public void WhenAttached_ThenRepeatingGroupsBecomeChildSubjects()
    {
        // Act
        var model = CreateAttachedModel();

        // Assert
        var channels = Assert.IsType<SunSpecDynamicGroup[]>(GetProperty(model, "Channel").GetValue());
        Assert.Equal(new[] { 40106, 40107 }, channels.Select(channel => channel.BaseAddress));
        Assert.NotNull(channels[1].TryGetRegisteredSubject()!.TryGetProperty("A"));
    }

    [Fact]
    public void WhenAttached_ThenDefinitionIsNotARegistryProperty()
    {
        // Act
        var model = CreateAttachedModel();

        // Assert
        var channel = GetChannels(model)[0];
        foreach (var subject in new IInterceptorSubject[] { model, channel })
        {
            var names = subject.TryGetRegisteredSubject()!.Properties.Select(property => property.Name).ToArray();
            Assert.DoesNotContain("Definition", names);
            Assert.DoesNotContain("Values", names);
        }
    }

    [Fact]
    public void WhenValueIsSetFromSource_ThenItIsReadBackAndTheChangeIsPublished()
    {
        // Arrange
        var model = CreateAttachedModel();
        var power = GetProperty(model, "W");
        var changes = new List<SubjectPropertyChange>();
        using var subscription = ((IInterceptorSubject)model).Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act
        power.Reference.SetValueFromSource(this, null, null, 1500m);

        // Assert
        Assert.Equal(1500m, power.GetValue());
        var change = Assert.Single(changes, change => change.Property == power.Reference);
        Assert.Equal(1500m, change.GetNewValue<decimal?>());
    }

    [Fact]
    public void WhenEnsurePropertiesIsCalledTwice_ThenPropertiesAreAddedOnce()
    {
        // Arrange
        var model = CreateAttachedModel();
        var propertyCount = model.TryGetRegisteredSubject()!.Properties.Length;
        var channels = GetChannels(model);

        // Act
        model.EnsureProperties();

        // Assert
        Assert.Equal(propertyCount, model.TryGetRegisteredSubject()!.Properties.Length);
        Assert.Same(channels, GetChannels(model));
    }

    [Fact]
    public void WhenPropertiesAreAddedAgain_ThenAddingFailsWithoutReplacingAnyProperty()
    {
        // Arrange
        var model = CreateAttachedModel();
        var power = GetProperty(model, "W");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => SunSpecDynamicProperties.AddProperties(model, model.GetDefinition().Group, isTopLevel: true));
        Assert.Same(power, GetProperty(model, "W"));
    }

    [Fact]
    public void WhenRepeatingGroupHasNoLayout_ThenItsPropertyIsEmpty()
    {
        // Act
        var model = CreateAttachedModel(DefinitionJson, registers: null);

        // Assert
        Assert.Empty(GetChannels(model));
    }

    [Fact]
    public void WhenCountGrows_ThenExistingGroupsAreKeptAndNewGroupsGetProperties()
    {
        // Arrange
        var model = CreateAttachedModel();
        var channels = GetChannels(model);

        // Act
        ApplyLayout(model, [64999, 7, 1500, 0, 3, 0xFFFF, 10, 20, 30]);

        // Assert
        var grown = GetChannels(model);
        Assert.Equal(3, grown.Length);
        Assert.Same(channels[0], grown[0]);
        Assert.Same(channels[1], grown[1]);
        Assert.NotNull(grown[2].TryGetRegisteredSubject()!.TryGetProperty("A"));
    }

    [Fact]
    public void WhenCountShrinks_ThenRemainingGroupsAreKept()
    {
        // Arrange
        var model = CreateAttachedModel();
        var channels = GetChannels(model);

        // Act
        ApplyLayout(model, [64999, 5, 1500, 0, 1, 0xFFFF, 10]);

        // Assert
        Assert.Same(channels[0], Assert.Single(GetChannels(model)));
    }

    [Fact]
    public void WhenGroupPointUsesAModelScaleFactor_ThenProviderReturnsTheModelProperty()
    {
        // Arrange
        var model = CreateAttachedModel();
        var channels = GetChannels(model);

        // Act
        var scaleFactor = channels[0].TryGetScaleFactorProperty("A");

        // Assert
        Assert.Equal(new PropertyReference(model, "A_SF"), scaleFactor);
    }

    [Fact]
    public void WhenNestedGroupUsesAModelScaleFactor_ThenProviderReturnsTheModelProperty()
    {
        // Arrange
        var model = CreateNestedModel();
        var outer = Assert.Single((SunSpecDynamicGroup[])GetProperty(model, "String").GetValue()!);
        var cells = (SunSpecDynamicGroup[])GetProperty(outer, "Cell").GetValue()!;

        // Act
        var scaleFactor = cells[1].TryGetScaleFactorProperty("V");

        // Assert
        Assert.Equal(40110, cells[1].BaseAddress);
        Assert.Equal(new PropertyReference(model, "V_SF"), scaleFactor);
    }

    [Fact]
    public void WhenGroupOccursOnce_ThenItsPropertyHoldsOneGroup()
    {
        // Act
        var model = CreateNestedModel();

        // Assert
        var property = GetProperty(model, "Settings");
        Assert.Equal(typeof(SunSpecDynamicGroup), property.Type);
        var settings = Assert.IsType<SunSpecDynamicGroup>(property.GetValue());
        Assert.Equal(40106, settings.BaseAddress);
        Assert.NotNull(settings.TryGetRegisteredSubject()!.TryGetProperty("Hz"));
    }

    [Fact]
    public void WhenScaleFactorIsInTheOwnGroup_ThenProviderReturnsNullAndTheAttributeNamesIt()
    {
        // Arrange
        var model = CreateNestedModel();
        var settings = (SunSpecDynamicGroup)GetProperty(model, "Settings").GetValue()!;

        // Act
        var scaleFactor = settings.TryGetScaleFactorProperty("Hz");

        // Assert
        Assert.Null(scaleFactor);
        var attribute = Assert.Single(GetProperty(settings, "Hz").ReflectionAttributes.OfType<ModbusRegisterAttribute>());
        Assert.Equal("Hz_SF", attribute.ScaleFactorProperty);
    }

    [Fact]
    public void WhenPointIsADuration_ThenItsPropertyIsATimeSpan()
    {
        // Act
        var model = CreateNestedModel();

        // Assert
        Assert.Equal(typeof(TimeSpan?), GetProperty(model, "Tms").Type);
    }

    [Fact]
    public void WhenPointIsAnEnumeration_ThenItsPropertyIsTheRawDiscreteValue()
    {
        // Act
        var model = CreateNestedModel();

        // Assert
        var state = GetProperty(model, "St");
        Assert.Equal(typeof(ushort?), state.Type);
        Assert.True(Assert.Single(state.ReflectionAttributes.OfType<StateAttribute>()).IsDiscrete);
    }

    [Fact]
    public void WhenModelIsUnknown_ThenOnlyItsPositionIsKnown()
    {
        // Act
        var model = new SunSpecUnknownModel(64998, 40200, 10);

        // Assert
        Assert.Equal(64998, model.ModelId);
        Assert.Equal(40200, model.BaseAddress);
        Assert.Equal("Unknown model 64998", model.Title);
    }
}
