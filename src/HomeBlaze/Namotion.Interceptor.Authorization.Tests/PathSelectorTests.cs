using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class PathSelectorTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];
    private static readonly PermissionAction[] Write = [PermissionAction.Write];

    [Fact]
    public void WhenPathNamesASubject_ThenItAndItsDescendantsMatch()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"], nameof(Room.Lights))));
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(home, nameof(Home.Rooms))));
    }

    [Fact]
    public void WhenPathIsTheRoot_ThenEverythingMatchesIncludingTheRootsMembers()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.True(policy.CanRead(Member(home.Wifi!, nameof(Wifi.Ssid))));
    }

    [Fact]
    public void WhenPathNamesAChild_ThenTheRootDoesNotMatch()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms"])]));

        // Act & Assert
        Assert.False(policy.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.False(policy.CanRead(Member(home, nameof(Home.Wifi))));
        Assert.True(policy.CanRead(Member(home.Rooms["Bedroom"], nameof(Room.Lights))));
    }

    [Fact]
    public void WhenPathUsesWildcards_ThenMatchingSubjectsAreSelected()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Writer", allow: [Rule(Write, paths: ["/**/Lights[C*]"])]));

        // Act & Assert
        Assert.True(policy.CanWrite(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.True(policy.CanWrite(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.IsOn))));
        Assert.False(policy.CanWrite(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn))));
    }

    [Fact]
    public void WhenDenyNamesABranch_ThenAllowAboveItDoesNotReachInto()
    {
        // Arrange
        var home = Create();
        var role = Role("Operator",
            allow: [Rule(Write, types: ["ILight"])],
            deny: [Rule(Write, paths: ["/Rooms[Bedroom]"])]);
        var policy = Compile(role);

        // Act & Assert
        Assert.True(policy.CanWrite(Member(home.Rooms["Kitchen"].Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.False(policy.CanWrite(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn))));
    }

    [Fact]
    public void WhenSubjectHasTwoPaths_ThenADenyOnEitherDenies()
    {
        // Arrange
        var home = Create();
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        home.Spare = ceiling;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Spare"])]));

        // Act & Assert
        Assert.Equal(2, Registered(ceiling).Parents.Length);
        Assert.False(policy.CanRead(Member(ceiling, nameof(Light.Brightness))));
        Assert.True(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.Brightness))));

        var otherBranch = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Bedroom]"])]));
        Assert.True(otherBranch.CanRead(Member(ceiling, nameof(Light.Brightness))));
    }

    [Fact]
    public void WhenSubjectHasTwoPaths_ThenAnAllowOnEitherAllows()
    {
        // Arrange
        var home = Create();
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        home.Spare = ceiling;
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Spare"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Member(ceiling, nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(home.Rooms["Kitchen"].Lights["Counter"], nameof(Light.Brightness))));
    }

    [Fact]
    public void WhenPathAndOtherSelectorsAreCombined_ThenAllMustMatch()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]"], types: ["ILight"], members: ["IsOn"])]));
        var kitchen = home.Rooms["Kitchen"];

        // Act & Assert
        Assert.True(policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.IsOn))));
        Assert.False(policy.CanRead(Member(kitchen.Lights["Ceiling"], nameof(Light.Brightness))));
        Assert.False(policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature))));
        Assert.False(policy.CanRead(Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.IsOn))));
    }
}
