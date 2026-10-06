using System.Text;
using Namotion.Devices.SunSpec.Definitions;

namespace Namotion.Devices.SunSpec.Tests.Definitions;

public class SunSpecLayoutTests
{
    // ID, L, N, A_SF (4 registers), then "channel" groups of A (1 register) and B (uint32, 2 registers), counted by N.
    private const string CountedJson = """
        { "id": 64900, "group": { "name": "counted", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
            { "name": "N", "type": "count", "size": 1 }, { "name": "A_SF", "type": "sunssf", "size": 1 } ],
          "groups": [ { "name": "channel", "count": "N", "points": [
            { "name": "A", "type": "uint16", "size": 1, "sf": "A_SF" }, { "name": "B", "type": "uint32", "size": 2 } ] } ] } }
        """;

    // ID, L, then "row" groups of one register filling the model.
    private const string FillJson = """
        { "id": 64901, "group": { "name": "fill", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 } ],
          "groups": [ { "name": "row", "count": 0, "points": [ { "name": "V", "type": "uint16", "size": 1 } ] } ] } }
        """;

    // ID, L, then "pair" groups of two registers filling the model.
    private const string FillPairJson = """
        { "id": 64902, "group": { "name": "fill", "points": [
            { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 } ],
          "groups": [ { "name": "pair", "count": 0, "points": [ { "name": "V", "type": "uint32", "size": 2 } ] } ] } }
        """;

    private static SunSpecModelDefinition Parse(string json) => SunSpecDefinitions.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void WhenGroupIsCountedByAPoint_ThenInstancesFollowEachOther()
    {
        // Arrange
        var definition = Parse(CountedJson);
        ushort[] registers = [64900, 8, 2, 0xFFFE, 0, 0, 0, 0, 0, 0];

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40100, registers);

        // Assert
        var channels = layout.GetGroup("channel");
        Assert.Equal(2, channels.Count);
        Assert.Equal(40104, channels[0].Address);
        Assert.Equal(40107, channels[1].Address);
        Assert.Equal(1, channels[1].Index);
        Assert.Equal(3, channels[0].Length);
        Assert.Equal(10, layout.Length);
    }

    [Fact]
    public void WhenGroupFillsTheModel_ThenItRepeatsUntilTheModelEnds()
    {
        // Arrange
        var definition = Parse(FillJson);
        ushort[] registers = [64901, 3, 1, 2, 3];

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        Assert.Equal(new[] { 40002, 40003, 40004 }, layout.GetGroup("row").Select(row => row.Address));
    }

    [Fact]
    public void WhenFillGroupDoesNotFitTheRemainingLength_ThenThePartialInstanceIsDropped()
    {
        // Arrange
        var definition = Parse(FillPairJson);
        ushort[] registers = [64902, 3, 1, 2, 3];

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        Assert.Equal(new[] { 40002 }, layout.GetGroup("pair").Select(pair => pair.Address));
    }

    [Fact]
    public void WhenCountedGroupsRunPastTheModel_ThenResolvingFails()
    {
        // Arrange
        var definition = Parse(CountedJson);
        ushort[] registers = [64900, 5, 3, 0, 0, 0, 0];

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => SunSpecLayout.Resolve(definition, 40000, registers));
    }

    [Fact]
    public void WhenDeviceReportsAHugeCount_ThenResolvingFails()
    {
        // Arrange
        var definition = Parse(CountedJson);
        ushort[] registers = [64900, 2, 0xFFFE, 0];

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => SunSpecLayout.Resolve(definition, 40000, registers));
    }

    [Fact]
    public void WhenCountPointLiesPastTheModel_ThenResolvingFails()
    {
        // Arrange
        var definition = Parse(CountedJson);
        ushort[] registers = [64900, 0];

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => SunSpecLayout.Resolve(definition, 40000, registers));
    }

    [Fact]
    public void WhenCountIsNotImplemented_ThenGroupHasNoInstances()
    {
        // Arrange
        var definition = Parse(CountedJson);
        ushort[] registers = [64900, 2, 0xFFFF, 0];

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        Assert.Empty(layout.GetGroup("channel"));
    }

    [Fact]
    public void WhenModelHasNestedCountedGroups_ThenEachLevelIsResolved()
    {
        // Arrange
        var definition = SunSpecDefinitions.TryGetBuiltIn(705)!;
        var registers = CreateRegistersFor705(definition, curveCount: 2, pointCount: 2);

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        var curves = layout.GetGroup("Crv");
        Assert.Equal(2, curves.Count);
        Assert.All(curves, curve => Assert.Equal(2, curve.GetGroup("Pt").Count));
        Assert.Equal(curves[0].Address + curves[0].Length, curves[1].Address);
        Assert.Equal(registers.Length, layout.Length);
    }

    [Fact]
    public void WhenNestedCountsExceedTheModel_ThenResolvingFails()
    {
        // Arrange
        var definition = SunSpecDefinitions.TryGetBuiltIn(705)!;
        var registers = CreateRegistersFor705(definition, curveCount: 2, pointCount: 2);
        registers[OffsetOf(definition.Group, "NCrv")] = 60000;
        registers[OffsetOf(definition.Group, "NPt")] = 60000;

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => SunSpecLayout.Resolve(definition, 40000, registers));
    }

    [Fact]
    public void WhenCountPointIsTwoLevelsUp_ThenItCountsTheNestedGroup()
    {
        // Arrange
        var definition = SunSpecDefinitions.TryGetBuiltIn(707)!;
        var top = definition.Group;
        var curve = top.Groups.Single(group => group.Name == "Crv");
        var mustTrip = curve.Groups.Single(group => group.Name == "MustTrip");
        var pointSize = mustTrip.Groups.Single(group => group.Name == "Pt").Points.Sum(p => p.Size);
        var curveSize = curve.Points.Sum(p => p.Size) + curve.Groups.Sum(group => group.Points.Sum(p => p.Size) + 3 * pointSize);
        var registers = new ushort[top.Points.Sum(p => p.Size) + 2 * curveSize];
        registers[0] = 707;
        registers[1] = (ushort)(registers.Length - 2);
        registers[OffsetOf(top, "NCrvSet")] = 2;
        registers[OffsetOf(top, "NPt")] = 3;

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        var curves = layout.GetGroup("Crv");
        Assert.Equal(2, curves.Count);
        Assert.All(curves, instance => Assert.Equal(3, instance.GetGroup("MustTrip").Single().GetGroup("Pt").Count));
        Assert.Equal(40000 + registers.Length - pointSize, curves[1].GetGroup("MomCess").Single().GetGroup("Pt")[2].Address);
        Assert.Equal(registers.Length, layout.Length);
    }

    [Fact]
    public void WhenGroupHasAFixedCount_ThenItRepeatsThatOften()
    {
        // Arrange
        var definition = Parse("""
            { "id": 64903, "group": { "name": "fixed", "points": [
                { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 } ],
              "groups": [ { "name": "slot", "count": 3, "points": [ { "name": "V", "type": "uint32", "size": 2 } ] } ] } }
            """);
        ushort[] registers = [64903, 6, 0, 0, 0, 0, 0, 0];

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        Assert.Equal(new[] { 40002, 40004, 40006 }, layout.GetGroup("slot").Select(slot => slot.Address));
    }

    [Fact]
    public void WhenANearerGroupDefinesTheCountPoint_ThenTheNearerPointIsUsed()
    {
        // Arrange
        var definition = Parse("""
            { "id": 64904, "group": { "name": "shadow", "points": [
                { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
                { "name": "N", "type": "count", "size": 1 } ],
              "groups": [ { "name": "outer", "count": "N", "points": [ { "name": "N", "type": "count", "size": 1 } ],
                "groups": [ { "name": "inner", "count": "N", "points": [ { "name": "V", "type": "uint16", "size": 1 } ] } ] } ] } }
            """);
        ushort[] registers = [64904, 5, 1, 3, 0, 0, 0];

        // Act
        var layout = SunSpecLayout.Resolve(definition, 40000, registers);

        // Assert
        var outer = Assert.Single(layout.GetGroup("outer"));
        Assert.Equal(3, outer.GetGroup("inner").Count);
    }

    [Fact]
    public void WhenCountedGroupHasNoRegisters_ThenResolvingFails()
    {
        // Arrange
        var definition = Parse("""
            { "id": 64905, "group": { "name": "empty", "points": [
                { "name": "ID", "type": "uint16", "size": 1 }, { "name": "L", "type": "uint16", "size": 1 },
                { "name": "N", "type": "count", "size": 1 } ],
              "groups": [ { "name": "nothing", "count": "N", "points": [] } ] } }
            """);
        ushort[] registers = [64905, 1, 2];

        // Act & Assert
        var exception = Assert.Throws<InvalidDataException>(() => SunSpecLayout.Resolve(definition, 40000, registers));
        Assert.Contains("has no registers", exception.Message);
    }

    private static ushort[] CreateRegistersFor705(SunSpecModelDefinition definition, int curveCount, int pointCount)
    {
        var top = definition.Group;
        var curve = top.Groups[0];
        var point = curve.Groups[0];
        var curveSize = curve.Points.Sum(p => p.Size) + pointCount * point.Points.Sum(p => p.Size);
        var registers = new ushort[top.Points.Sum(p => p.Size) + curveCount * curveSize];
        registers[0] = 705;
        registers[1] = (ushort)(registers.Length - 2);
        registers[OffsetOf(top, "NCrv")] = (ushort)curveCount;
        registers[OffsetOf(top, "NPt")] = (ushort)pointCount;
        return registers;
    }

    private static int OffsetOf(SunSpecGroupDefinition group, string pointName)
        => group.Points.TakeWhile(p => p.Name != pointName).Sum(p => p.Size);
}
