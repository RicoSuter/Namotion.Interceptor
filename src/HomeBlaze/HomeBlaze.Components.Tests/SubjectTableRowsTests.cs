using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectTableRowsTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

    private sealed record Graph(SubjectPathResolver Resolver, SubjectTable Table, TestDevice Pump, TestDevice Fan, Card Note);

    // The table stands next to the folder "devices", so "../devices" leads from the table to the folder.
    private Graph CreateGraph()
    {
        var pump = new TestDevice(_context) { Name = "Pump", Speed = 1200 };
        var fan = new TestDevice(_context) { Name = "Fan", Speed = 800 };
        var note = new Card(_context) { Title = "Note" };
        var devices = new TestFolder(_context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Pump"] = pump, ["Fan 2"] = fan, ["Note"] = note },
            Devices = [new TestDevice(_context) { Name = "First" }, new TestDevice(_context) { Name = "Second" }],
            DevicesByName = new Dictionary<string, TestDevice> { ["third"] = new TestDevice(_context) { Name = "Third" } },
            Main = new TestDevice(_context) { Name = "Main" },
            Label = "Devices"
        };
        var table = new SubjectTable(_context);
        var root = new TestFolder(_context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["devices"] = devices, ["table"] = table }
        };

        return new Graph(new SubjectPathResolver(() => root), table, pump, fan, note);
    }

    private static string[] GetNames(IEnumerable<SubjectPropertyChild> rows) =>
        rows.Select(SubjectTableRows.GetTitle).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void WhenSourceIsAFolder_ThenRowsAreItsChildren()
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        var found = SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, "../devices", null, out var rows, out var error);

        // Assert
        Assert.True(found);
        Assert.Null(error);
        Assert.Equal(["Fan", "Note", "Pump"], GetNames(rows));
    }

    [Theory]
    [InlineData("../devices/Devices", new[] { "First", "Second" })]
    [InlineData("../devices/DevicesByName", new[] { "Third" })]
    [InlineData("../devices/Main", new[] { "Main" })]
    public void WhenSourceIsAPropertyThatHoldsSubjects_ThenRowsAreItsSubjects(string source, string[] expected)
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        var found = SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, source, null, out var rows, out var error);

        // Assert
        Assert.True(found);
        Assert.Null(error);
        Assert.Equal(expected, GetNames(rows));
    }

    [Theory]
    [InlineData("../devices/Pump")]
    [InlineData("../devices/Label")]
    [InlineData("../nothing")]
    [InlineData("[")]
    public void WhenSourceIsNoFolderAndNoPropertyThatHoldsSubjects_ThenItIsAnError(string source)
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        var found = SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, source, null, out var rows, out var error);

        // Assert
        Assert.False(found);
        Assert.Empty(rows);
        Assert.Equal($"Cannot list subjects of: {source}", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WhenSourceIsNotSet_ThenItIsAnError(string? source)
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        var found = SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, source, null, out _, out var error);

        // Assert
        Assert.False(found);
        Assert.Equal(SubjectTableRows.MissingSourceError, error);
    }

    [Theory]
    [InlineData(typeof(TestDevice))]
    [InlineData(typeof(TestDeviceBase))]
    [InlineData(typeof(ITestDevice))]
    [InlineData(typeof(ITestSensor<>))]
    [InlineData(typeof(ITestSensor<int>))]
    public void WhenTypeIsTheClassABaseClassOrAnInterface_ThenOnlyRowsOfThatTypeRemain(Type type)
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, "../devices", type.FullName, out var rows, out _);

        // Assert
        Assert.Equal(["Fan", "Pump"], GetNames(rows));
    }

    [Theory]
    [InlineData("TestDevice")]
    [InlineData("homeblaze.components.tests.models.testdevice")]
    [InlineData("Nothing.Like.This")]
    public void WhenTypeIsNoFullNameOfTheRows_ThenNoRowRemainsAndThereIsNoError(string typeName)
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        var found = SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, "../devices", typeName, out var rows, out var error);

        // Assert
        Assert.True(found);
        Assert.Null(error);
        Assert.Empty(rows);
    }

    [Fact]
    public void WhenRowHasATitleAKeyOrNeither_ThenItsTitleFollowsThatOrder()
    {
        // Arrange
        var titled = new TestDevice(_context) { Name = "Pump" };
        var untitled = new TestDevice(_context);

        // Act
        var byTitle = SubjectTableRows.GetTitle(new SubjectPropertyChild { Subject = titled, Index = "pump" });
        var byKey = SubjectTableRows.GetTitle(new SubjectPropertyChild { Subject = untitled, Index = "spare" });
        var byType = SubjectTableRows.GetTitle(new SubjectPropertyChild { Subject = untitled, Index = null });

        // Assert
        Assert.Equal("Pump", byTitle);
        Assert.Equal("spare", byKey);
        Assert.Equal(nameof(TestDevice), byType);
    }

    [Fact]
    public void WhenColumnHasNoLabel_ThenHeaderIsTheTitleOfThePropertyOrItsName()
    {
        // Arrange
        var graph = CreateGraph();
        SubjectTableRows.TryGetRows(graph.Resolver, graph.Table, "../devices", typeof(TestDevice).FullName, out var rows, out _);
        var labelled = new SubjectTableColumn(_context) { Property = nameof(TestDevice.Speed), Label = "Speed" };
        var titled = new SubjectTableColumn(_context) { Property = nameof(TestDevice.Speed) };
        var unknown = new SubjectTableColumn(_context) { Property = "Missing" };

        // Act
        var labelledHeader = SubjectTableRows.GetHeader(labelled, rows);
        var titledHeader = SubjectTableRows.GetHeader(titled, rows);
        var unknownHeader = SubjectTableRows.GetHeader(unknown, rows);
        var withoutRowsHeader = SubjectTableRows.GetHeader(titled, []);

        // Assert
        Assert.Equal("Speed", labelledHeader);
        Assert.Equal("Rotation speed", titledHeader);
        Assert.Equal("Missing", unknownHeader);
        Assert.Equal(nameof(TestDevice.Speed), withoutRowsHeader);
    }

    [Fact]
    public void WhenRowHasAPath_ThenItLinksToTheSubjectBrowserWithEscapedSegments()
    {
        // Arrange
        var graph = CreateGraph();

        // Act
        var pumpLink = SubjectTableRows.GetLink(graph.Resolver, graph.Pump);
        var fanLink = SubjectTableRows.GetLink(graph.Resolver, graph.Fan);
        var detachedLink = SubjectTableRows.GetLink(graph.Resolver, new TestDevice(_context));

        // Assert
        Assert.Equal("/browser/devices/Pump", pumpLink);
        Assert.Equal("/browser/devices/Fan%202", fanLink);
        Assert.Null(detachedLink);
    }

    [Fact]
    public void WhenSubjectTableIsDescribed_ThenItsSourceOffersSubjectsAndItsColumnsAreSlotBlocks()
    {
        // Act
        var table = new SubjectTable(_context);
        var source = typeof(SubjectTable).GetProperty(nameof(SubjectTable.Source))!.GetCustomAttribute<SubjectPathAttribute>()!;
        var slot = typeof(SubjectTable).GetProperty(nameof(SubjectTable.Columns))!.GetCustomAttribute<SlotAttribute>()!;
        var map = typeof(SubjectTableColumn).GetProperty(nameof(SubjectTableColumn.Map))!.GetCustomAttribute<SubjectPathAttribute>()!;

        // Assert
        Assert.Empty(table.Columns);
        Assert.Equal(typeof(IInterceptorSubject), source.SubjectType);
        Assert.Equal("Column", slot.Name);
        Assert.Equal(typeof(ValueMap), map.SubjectType);
        Assert.NotNull(typeof(SubjectTableColumn).GetCustomAttribute<ExcludeFromCreationAttribute>());
    }
}
