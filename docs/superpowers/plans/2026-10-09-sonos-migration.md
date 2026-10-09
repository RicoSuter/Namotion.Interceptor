# Sonos Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the HomeBlaze v1 `HomeBlaze.Sonos` library with `Namotion.Devices.Sonos`, a source-generated subject library that models the Sonos household as players, satellites and groups, updated by our own UPnP event listener plus a reconciliation poll.

**Architecture:** `SonosSystem` (hosted `BackgroundService`) finds a seed speaker (configured host, known IPs, then SSDP), reads the zone topology from it, keeps one `Sonos.Base` connection per unit for SOAP calls, polls every 30 s and subscribes to UPnP events through an `HttpListener`-based `SonosEventListener`. Pure parsers turn topology, event and metadata XML into records that subjects apply in place, so all logic except the network edge is unit tested; the network edge is tested against a loopback fake speaker.

**Tech Stack:** .NET 10, C# 14, Namotion.Interceptor source generator, `Sonos.Base` 0.4.0, `Rssdp` 5.0.0, `System.Net.HttpListener`, LINQ to XML, xUnit, MudBlazor 9.

**Spec:** `docs/superpowers/specs/2026-10-09-sonos-migration-design.md`

## Global Constraints

- Target framework comes from `src/Directory.Build.props` (`net10.0`); do not set `TargetFramework` in new projects.
- Package versions live only in `src/Directory.Packages.props`: `Sonos.Base` `0.4.0`, `Rssdp` `5.0.0`. Do not reference `Sonos.Base.Events.Http`.
- Build must have 0 warnings (warnings are errors). Sonar findings: fix, or ask the user before adding any suppression.
- No `XmlSerializer` in our code; parse XML with LINQ to XML (`XDocument`), which is reflection free.
- `InternalsVisibleTo` only for `Namotion.Devices.Sonos.Tests`.
- All `[InterceptorSubject]` stored properties are `partial`, initialized in constructors, state setters `internal set`, collections replaced and never mutated, subjects updated in place and never recreated by the poll.
- Inject and use `ILogger<T>`; never swallow an exception without logging, except where a method's contract is "return null for unparsable third-party data" (documented at the method).
- Tests: `When<Condition>_Then<Expected>` names, `// Arrange`, `// Act`, `// Assert` comments, no `Task.Delay`/`Thread.Sleep` (use `AsyncTestHelpers.WaitUntilAsync` or `TaskCompletionSource`).
- Docs and comments: no em dashes, no hard wrapping in markdown, no mention of HomeBlaze outside `src/HomeBlaze` (this whole library lives under `src/HomeBlaze`, so HomeBlaze may be named in `Sonos.md`).
- Commit messages: conventional prefix (`feat:`, `test:`, `docs:`, `chore:`), ending with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Never call a write operation on the user's real speakers without asking first. Live checks in Task 13 are read-only until the user approves.

## File Structure

```
src/HomeBlaze/HomeBlaze.Abstractions/Media/
  IAudioPlayerState.cs            (modify) keeps IsPlaying, IsMuted
  IMediaTrackState.cs             (create) current track title, artist, album, art, URI, position, duration

src/HomeBlaze/Namotion.Devices.Sonos/
  Namotion.Devices.Sonos.csproj
  SonosTransportState.cs, SonosSource.cs, SonosRepeatMode.cs, SonosSatelliteRole.cs   public enums
  SonosValues.cs                  wire value conversions (volume, durations, play mode, source, battery, URIs, DIDL)
  Parsing/SonosTopology.cs        topology records
  Parsing/ZoneGroupStateParser.cs ZoneGroupState XML to SonosTopology
  Parsing/ServiceChanges.cs       AvTransportChange, RenderingControlChange, GroupRenderingControlChange, SonosPlayerReading
  Parsing/UpnpEventParser.cs      NOTIFY bodies to service changes
  Parsing/DidlParser.cs           DIDL-Lite track metadata
  Parsing/FavoritesParser.cs      Browse FV:2 result to playable favorites
  Parsing/DeviceDescriptionParser.cs device_description.xml to model and service ids
  Events/SonosEventSubscription.cs
  Events/SonosEventListener.cs    HttpListener receiver plus SUBSCRIBE/RENEW/UNSUBSCRIBE
  Client/SonosClientProvider.cs   ISonosServiceProvider handing Sonos.Base the shared HttpClient
  Client/SonosConnection.cs       one Sonos.Base device: reads as records, commands
  Client/SonosDiscovery.cs        SSDP, seed host parsing, local address detection
  SonosDevice.cs                  base subject for players and satellites
  SonosSatellite.cs
  SonosPlayer.cs
  SonosGroup.cs
  SonosSystem.cs                  hosted subject: configuration, state, topology, operations
  SonosSystem.Runtime.cs          connection loop, polling, subscriptions, teardown
  SonosServiceCollectionExtensions.cs

src/HomeBlaze/Namotion.Devices.Sonos.Tests/
  Namotion.Devices.Sonos.Tests.csproj
  Fixtures/zone-group-state.xml, favorites.xml, device-description-ray.xml
  Testing/TestFixtures.cs, SonosEventBodies.cs, LoopbackHttpServer.cs, FakeSonosSpeaker.cs, TestHttpClientFactory.cs, ConnectedSystem.cs
  SonosValuesTests.cs, ZoneGroupStateParserTests.cs, UpnpEventParserTests.cs, DidlParserTests.cs,
  FavoritesParserTests.cs, DeviceDescriptionParserTests.cs, SonosEventListenerTests.cs,
  SonosSystemTopologyTests.cs, SonosPlayerStateTests.cs, SonosGroupStateTests.cs, SonosDerivedTrackingTests.cs,
  SonosConnectionTests.cs, SonosDiscoveryTests.cs, SonosSystemRuntimeTests.cs,
  SonosPlayerOperationTests.cs, SonosGroupOperationTests.cs, SonosSystemOperationTests.cs, SonosServiceCollectionExtensionsTests.cs

src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze/
  Namotion.Devices.Sonos.HomeBlaze.csproj, _Imports.razor
  SonosSystemWidget.razor, SonosPlayerWidget.razor, SonosGroupWidget.razor, SonosSystemEditComponent.razor

src/HomeBlaze/HomeBlaze/Data/Files/Docs/devices/Sonos.md
src/HomeBlaze/HomeBlaze/Data/Files/Devices/Sonos.json
src/Directory.Packages.props, src/Namotion.Interceptor.slnx, src/HomeBlaze/HomeBlaze/HomeBlaze.csproj, src/HomeBlaze/HomeBlaze/Program.cs   (modify)
```

Run every command from the repository root.

---

### Task 1: Move track state into IMediaTrackState

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Abstractions/Media/IAudioPlayerState.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Media/IMediaTrackState.cs`

**Interfaces:**
- Produces: `IAudioPlayerState { bool? IsPlaying; bool? IsMuted; }` (plus `IVolumeState.Volume`), `IMediaTrackState { string? CurrentTrackTitle; string? CurrentTrackArtist; string? CurrentTrackAlbum; string? CurrentTrackImageUri; string? CurrentTrackUri; TimeSpan? CurrentTrackPosition; TimeSpan? CurrentTrackDuration; }`

- [ ] **Step 1: Confirm nothing uses the members being moved**

Run: `grep -rnE "IAudioPlayerState|IAudioPlayer\b|CurrentTrack\b|\.Duration\b" src --include='*.cs' --include='*.razor' | grep -v "/bin/\|/obj/\|HomeBlaze.Abstractions/Media/"`
Expected: no lines that reference the audio player interfaces (unrelated `.Duration` hits outside media are fine).

- [ ] **Step 2: Replace `IAudioPlayerState.cs`**

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for audio players.
/// </summary>
[SubjectAbstraction]
[Description("Reports audio player playback state.")]
public interface IAudioPlayerState : IVolumeState
{
    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    [State(Position = 140)]
    bool? IsPlaying { get; }

    /// <summary>
    /// Whether audio is muted.
    /// </summary>
    [State(Position = 141)]
    bool? IsMuted { get; }
}
```

- [ ] **Step 3: Create `IMediaTrackState.cs`**

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for subjects that report the media track currently playing.
/// </summary>
[SubjectAbstraction]
[Description("Reports the currently playing media track.")]
public interface IMediaTrackState
{
    /// <summary>
    /// The title of the current track, or the stream text of a radio station.
    /// </summary>
    [State(Position = 146)]
    string? CurrentTrackTitle { get; }

    /// <summary>
    /// The artist of the current track.
    /// </summary>
    [State(Position = 147)]
    string? CurrentTrackArtist { get; }

    /// <summary>
    /// The album of the current track.
    /// </summary>
    [State(Position = 148)]
    string? CurrentTrackAlbum { get; }

    /// <summary>
    /// An absolute URI of the current track's artwork.
    /// </summary>
    [State(Position = 149)]
    string? CurrentTrackImageUri { get; }

    /// <summary>
    /// The URI of the current track.
    /// </summary>
    [State(Position = 150)]
    string? CurrentTrackUri { get; }

    /// <summary>
    /// The playback position within the current track.
    /// </summary>
    [State(Position = 151)]
    TimeSpan? CurrentTrackPosition { get; }

    /// <summary>
    /// The total duration of the current track, or null when it has none (streams, TV).
    /// </summary>
    [State(Position = 152)]
    TimeSpan? CurrentTrackDuration { get; }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/Media
git commit -m "feat: move current track state into IMediaTrackState

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Project scaffolding, enums and wire value conversions

**Files:**
- Modify: `src/Directory.Packages.props`, `src/Namotion.Interceptor.slnx`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/Namotion.Devices.Sonos.csproj`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/SonosTransportState.cs`, `SonosSource.cs`, `SonosRepeatMode.cs`, `SonosSatelliteRole.cs`, `SonosValues.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Namotion.Devices.Sonos.Tests.csproj`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosValuesTests.cs`

**Interfaces:**
- Produces (public enums in `Namotion.Devices.Sonos`): `SonosTransportState { Unknown, Stopped, Playing, Paused, Transitioning }`, `SonosSource { None, Tv, LineIn, SpotifyConnect, AirPlay, Radio, Queue, Other }`, `SonosRepeatMode { Off, All, One }`, `SonosSatelliteRole { Other, Subwoofer, RearLeft, RearRight, StereoPartner }`.
- Produces (`internal static class SonosValues`): `const string NotImplemented`, `const int DevicePort = 1400`, `bool IsKnown(string?)`, `string? NullIfEmpty(string?)`, `decimal ToVolume(int)`, `int ToSonosVolume(decimal)`, `int ToSonosVolumeAdjustment(decimal)`, `TimeSpan? ParseDuration(string?)`, `string FormatDuration(TimeSpan)`, `SonosTransportState ParseTransportState(string?)`, `(bool Shuffle, SonosRepeatMode Repeat)? ParsePlayMode(string?)`, `string FormatPlayMode(bool, SonosRepeatMode)`, `SonosSource DetectSource(string?)`, `(decimal? Level, bool? IsCharging) ParseBattery(string?)`, `string? ToAbsoluteUri(string?, Uri?)`, `string ToStreamUri(string)`, `string CreateStreamMetadata(string)`.

- [ ] **Step 1: Add package versions**

In `src/Directory.Packages.props`, add these two lines inside the `<ItemGroup>` in alphabetical position (after the `Microsoft.*`/`MudBlazor` entries where they sort):

```xml
    <PackageVersion Include="Rssdp" Version="5.0.0" />
    <PackageVersion Include="Sonos.Base" Version="0.4.0" />
```

- [ ] **Step 2: Create the device project**

`src/HomeBlaze/Namotion.Devices.Sonos/Namotion.Devices.Sonos.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <InternalsVisibleTo Include="Namotion.Devices.Sonos.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Rssdp" />
    <PackageReference Include="Sonos.Base" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Hosting\Namotion.Interceptor.Hosting.csproj" />
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create the test project**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Namotion.Devices.Sonos.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Devices.Sonos\Namotion.Devices.Sonos.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Add both projects to the solution**

In `src/Namotion.Interceptor.slnx`, directly after the three `Namotion.Devices.MyStrom` `<Project>` lines, add:

```xml
    <Project Path="HomeBlaze/Namotion.Devices.Sonos/Namotion.Devices.Sonos.csproj" />
    <Project Path="HomeBlaze/Namotion.Devices.Sonos.Tests/Namotion.Devices.Sonos.Tests.csproj" />
```

- [ ] **Step 5: Create the enums**

`src/HomeBlaze/Namotion.Devices.Sonos/SonosTransportState.cs`:

```csharp
namespace Namotion.Devices.Sonos;

/// <summary>
/// The transport state of a Sonos player.
/// </summary>
public enum SonosTransportState
{
    Unknown,
    Stopped,
    Playing,
    Paused,
    Transitioning
}
```

`src/HomeBlaze/Namotion.Devices.Sonos/SonosSource.cs`:

```csharp
namespace Namotion.Devices.Sonos;

/// <summary>
/// Where the audio of a Sonos player comes from.
/// </summary>
public enum SonosSource
{
    None,
    Tv,
    LineIn,
    SpotifyConnect,
    AirPlay,
    Radio,
    Queue,
    Other
}
```

`src/HomeBlaze/Namotion.Devices.Sonos/SonosRepeatMode.cs`:

```csharp
namespace Namotion.Devices.Sonos;

/// <summary>
/// The repeat mode of a Sonos player.
/// </summary>
public enum SonosRepeatMode
{
    Off,
    All,
    One
}
```

`src/HomeBlaze/Namotion.Devices.Sonos/SonosSatelliteRole.cs`:

```csharp
namespace Namotion.Devices.Sonos;

/// <summary>
/// The role a bonded unit plays for its room player.
/// </summary>
public enum SonosSatelliteRole
{
    Other,
    Subwoofer,
    RearLeft,
    RearRight,
    StereoPartner
}
```

- [ ] **Step 6: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosValuesTests.cs`:

```csharp
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosValuesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(44, 0.44)]
    [InlineData(100, 1)]
    [InlineData(150, 1)]
    public void WhenConvertingSonosVolume_ThenReturnsFraction(int sonosVolume, double expected)
    {
        // Act
        var volume = SonosValues.ToVolume(sonosVolume);

        // Assert
        Assert.Equal((decimal)expected, volume);
    }

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(0.444, 44)]
    [InlineData(0.445, 45)]
    [InlineData(-1, 0)]
    [InlineData(2, 100)]
    public void WhenConvertingVolumeFraction_ThenReturnsClampedSonosVolume(double volume, int expected)
    {
        // Act
        var sonosVolume = SonosValues.ToSonosVolume((decimal)volume);

        // Assert
        Assert.Equal(expected, sonosVolume);
    }

    [Theory]
    [InlineData(0.05, 5)]
    [InlineData(-0.1, -10)]
    [InlineData(-3, -100)]
    public void WhenConvertingVolumeDelta_ThenReturnsClampedAdjustment(double delta, int expected)
    {
        // Act
        var adjustment = SonosValues.ToSonosVolumeAdjustment((decimal)delta);

        // Assert
        Assert.Equal(expected, adjustment);
    }

    [Theory]
    [InlineData("0:03:25", 205)]
    [InlineData("00:00:01", 1)]
    [InlineData("26:00:00", 93600)]
    [InlineData("0:00:10.500", 10.5)]
    public void WhenParsingDuration_ThenReturnsTimeSpan(string value, double expectedSeconds)
    {
        // Act
        var duration = SonosValues.ParseDuration(value);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("garbage")]
    public void WhenParsingUnknownDuration_ThenReturnsNull(string? value)
    {
        // Act
        var duration = SonosValues.ParseDuration(value);

        // Assert
        Assert.Null(duration);
    }

    [Fact]
    public void WhenFormattingDuration_ThenUsesTwoDigitHours()
    {
        // Act
        var formatted = SonosValues.FormatDuration(new TimeSpan(1, 2, 3));

        // Assert
        Assert.Equal("01:02:03", formatted);
    }

    [Theory]
    [InlineData("PLAYING", SonosTransportState.Playing)]
    [InlineData("PAUSED_PLAYBACK", SonosTransportState.Paused)]
    [InlineData("STOPPED", SonosTransportState.Stopped)]
    [InlineData("TRANSITIONING", SonosTransportState.Transitioning)]
    [InlineData("SOMETHING_ELSE", SonosTransportState.Unknown)]
    [InlineData(null, SonosTransportState.Unknown)]
    public void WhenParsingTransportState_ThenMapsToEnum(string? value, SonosTransportState expected)
    {
        // Act
        var state = SonosValues.ParseTransportState(value);

        // Assert
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData("NORMAL", false, SonosRepeatMode.Off)]
    [InlineData("REPEAT_ALL", false, SonosRepeatMode.All)]
    [InlineData("REPEAT_ONE", false, SonosRepeatMode.One)]
    [InlineData("SHUFFLE_NOREPEAT", true, SonosRepeatMode.Off)]
    [InlineData("SHUFFLE", true, SonosRepeatMode.All)]
    [InlineData("SHUFFLE_REPEAT_ONE", true, SonosRepeatMode.One)]
    public void WhenParsingAndFormattingPlayMode_ThenRoundTrips(string playMode, bool shuffle, SonosRepeatMode repeat)
    {
        // Act
        var parsed = SonosValues.ParsePlayMode(playMode);
        var formatted = SonosValues.FormatPlayMode(shuffle, repeat);

        // Assert
        Assert.Equal((shuffle, repeat), parsed);
        Assert.Equal(playMode, formatted);
    }

    [Fact]
    public void WhenParsingUnknownPlayMode_ThenReturnsNull()
    {
        // Act
        var parsed = SonosValues.ParsePlayMode("NOT_IMPLEMENTED");

        // Assert
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData(null, SonosSource.None)]
    [InlineData("", SonosSource.None)]
    [InlineData("x-sonos-htastream:RINCON_A0000000000701400:spdif", SonosSource.Tv)]
    [InlineData("x-rincon-stream:RINCON_A0000000000601400", SonosSource.LineIn)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf", SonosSource.SpotifyConnect)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:1,airplay:4F9A2B", SonosSource.AirPlay)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:3,unknown:1", SonosSource.Other)]
    [InlineData("x-rincon-mp3radio://stream.example.com/live.mp3", SonosSource.Radio)]
    [InlineData("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", SonosSource.Radio)]
    [InlineData("aac://https://stream.example.com/live", SonosSource.Radio)]
    [InlineData("https://stream.example.com/live.mp3", SonosSource.Radio)]
    [InlineData("x-rincon-queue:RINCON_A0000000000601400#0", SonosSource.Queue)]
    [InlineData("x-rincon:RINCON_A0000000000101400", SonosSource.Other)]
    public void WhenDetectingSource_ThenMapsUriScheme(string? uri, SonosSource expected)
    {
        // Act
        var source = SonosValues.DetectSource(uri);

        // Assert
        Assert.Equal(expected, source);
    }

    [Fact]
    public void WhenParsingBatteryInfo_ThenReturnsLevelAndCharging()
    {
        // Act
        var (level, isCharging) = SonosValues.ParseBattery("RawBattPct:100,BattPct:87,BattChg:CHARGING,BattTmp:23");

        // Assert
        Assert.Equal(0.87m, level);
        Assert.True(isCharging);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingElse:1")]
    public void WhenParsingMissingBatteryInfo_ThenReturnsNulls(string? moreInfo)
    {
        // Act
        var (level, isCharging) = SonosValues.ParseBattery(moreInfo);

        // Assert
        Assert.Null(level);
        Assert.Null(isCharging);
    }

    [Fact]
    public void WhenBatteryNotCharging_ThenIsChargingIsFalse()
    {
        // Act
        var (_, isCharging) = SonosValues.ParseBattery("BattPct:40,BattChg:NOT_CHARGING");

        // Assert
        Assert.False(isCharging);
    }

    [Fact]
    public void WhenUriIsRelative_ThenItIsResolvedAgainstTheSpeaker()
    {
        // Act
        var uri = SonosValues.ToAbsoluteUri("/getaa?s=1&u=x-sonos-spotify", new Uri("http://10.0.0.121:1400/"));

        // Assert
        Assert.Equal("http://10.0.0.121:1400/getaa?s=1&u=x-sonos-spotify", uri);
    }

    [Fact]
    public void WhenUriIsAbsolute_ThenItIsKept()
    {
        // Act
        var uri = SonosValues.ToAbsoluteUri("https://images.example.com/cover.jpg", new Uri("http://10.0.0.121:1400/"));

        // Assert
        Assert.Equal("https://images.example.com/cover.jpg", uri);
    }

    [Fact]
    public void WhenConvertingHttpStream_ThenUsesRadioScheme()
    {
        // Act
        var uri = SonosValues.ToStreamUri("http://stream.example.com/live.mp3");

        // Assert
        Assert.Equal("x-rincon-mp3radio://stream.example.com/live.mp3", uri);
    }

    [Fact]
    public void WhenCreatingStreamMetadata_ThenTitleIsEscaped()
    {
        // Act
        var metadata = SonosValues.CreateStreamMetadata("Rock & <Roll>");

        // Assert
        Assert.Contains("<dc:title>Rock &amp; &lt;Roll&gt;</dc:title>", metadata);
        Assert.Contains("object.item.audioItem.audioBroadcast", metadata);
    }
}
```

- [ ] **Step 7: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests`
Expected: build fails with `CS0103: The name 'SonosValues' does not exist`.

- [ ] **Step 8: Implement `SonosValues.cs`**

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Conversions between Sonos UPnP wire values and subject values.
/// </summary>
internal static class SonosValues
{
    internal const string NotImplemented = "NOT_IMPLEMENTED";

    internal const int DevicePort = 1400;

    /// <summary>
    /// A value Sonos actually reported. Null means the field was absent and <c>NOT_IMPLEMENTED</c> means the
    /// source cannot tell (Spotify Connect, TV), so both keep the current value rather than clearing it.
    /// </summary>
    internal static bool IsKnown([NotNullWhen(true)] string? value) =>
        value is not null && value != NotImplemented;

    internal static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    internal static decimal ToVolume(int sonosVolume) =>
        Math.Clamp(sonosVolume, 0, 100) / 100m;

    internal static int ToSonosVolume(decimal volume) =>
        (int)Math.Round(Math.Clamp(volume, 0m, 1m) * 100m, MidpointRounding.AwayFromZero);

    internal static int ToSonosVolumeAdjustment(decimal delta) =>
        (int)Math.Round(Math.Clamp(delta, -1m, 1m) * 100m, MidpointRounding.AwayFromZero);

    internal static TimeSpan? ParseDuration(string? value)
    {
        if (!IsKnown(value) || value.Length == 0)
        {
            return null;
        }

        var parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    internal static string FormatDuration(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}");

    internal static SonosTransportState ParseTransportState(string? value) => value switch
    {
        "PLAYING" => SonosTransportState.Playing,
        "PAUSED_PLAYBACK" => SonosTransportState.Paused,
        "STOPPED" => SonosTransportState.Stopped,
        "TRANSITIONING" => SonosTransportState.Transitioning,
        _ => SonosTransportState.Unknown
    };

    internal static (bool Shuffle, SonosRepeatMode Repeat)? ParsePlayMode(string? value) => value switch
    {
        "NORMAL" => (false, SonosRepeatMode.Off),
        "REPEAT_ALL" => (false, SonosRepeatMode.All),
        "REPEAT_ONE" => (false, SonosRepeatMode.One),
        "SHUFFLE_NOREPEAT" => (true, SonosRepeatMode.Off),
        "SHUFFLE" => (true, SonosRepeatMode.All),
        "SHUFFLE_REPEAT_ONE" => (true, SonosRepeatMode.One),
        _ => null
    };

    internal static string FormatPlayMode(bool shuffle, SonosRepeatMode repeat) => (shuffle, repeat) switch
    {
        (false, SonosRepeatMode.Off) => "NORMAL",
        (false, SonosRepeatMode.All) => "REPEAT_ALL",
        (false, SonosRepeatMode.One) => "REPEAT_ONE",
        (true, SonosRepeatMode.Off) => "SHUFFLE_NOREPEAT",
        (true, SonosRepeatMode.All) => "SHUFFLE",
        (true, SonosRepeatMode.One) => "SHUFFLE_REPEAT_ONE",
        _ => throw new ArgumentOutOfRangeException(nameof(repeat), repeat, "Unknown repeat mode.")
    };

    internal static SonosSource DetectSource(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return SonosSource.None;
        }

        if (uri.StartsWith("x-sonos-htastream:", StringComparison.Ordinal))
        {
            return SonosSource.Tv;
        }

        if (uri.StartsWith("x-rincon-stream:", StringComparison.Ordinal))
        {
            return SonosSource.LineIn;
        }

        if (uri.StartsWith("x-rincon-queue:", StringComparison.Ordinal))
        {
            return SonosSource.Queue;
        }

        if (uri.StartsWith("x-sonos-vli:", StringComparison.Ordinal))
        {
            if (uri.Contains(",spotify:", StringComparison.Ordinal))
            {
                return SonosSource.SpotifyConnect;
            }

            return uri.Contains(",airplay:", StringComparison.Ordinal) ? SonosSource.AirPlay : SonosSource.Other;
        }

        if (uri.StartsWith("x-rincon-mp3radio:", StringComparison.Ordinal) ||
            uri.StartsWith("x-sonosapi-stream:", StringComparison.Ordinal) ||
            uri.StartsWith("x-sonosapi-radio:", StringComparison.Ordinal) ||
            uri.StartsWith("x-sonosapi-hls:", StringComparison.Ordinal) ||
            uri.StartsWith("aac:", StringComparison.Ordinal) ||
            uri.StartsWith("hls-radio:", StringComparison.Ordinal) ||
            uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return SonosSource.Radio;
        }

        return SonosSource.Other;
    }

    /// <summary>
    /// Reads the battery from a topology <c>MoreInfo</c> value such as <c>BattPct:87,BattChg:CHARGING</c>.
    /// </summary>
    internal static (decimal? Level, bool? IsCharging) ParseBattery(string? moreInfo)
    {
        decimal? level = null;
        bool? isCharging = null;
        if (string.IsNullOrEmpty(moreInfo))
        {
            return (level, isCharging);
        }

        foreach (var entry in moreInfo.Split(','))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator];
            var value = entry[(separator + 1)..];
            if (key == "BattPct" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
            {
                level = Math.Clamp(percent, 0, 100) / 100m;
            }
            else if (key == "BattChg")
            {
                isCharging = value == "CHARGING";
            }
        }

        return (level, isCharging);
    }

    /// <summary>
    /// Resolves the speaker-relative paths Sonos uses for album art (<c>/getaa?...</c>) against the speaker.
    /// </summary>
    internal static string? ToAbsoluteUri(string? uri, Uri? baseUri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        return baseUri is not null && uri.StartsWith('/') ? new Uri(baseUri, uri).AbsoluteUri : uri;
    }

    /// <summary>
    /// Sonos renders a plain http(s) stream as radio, with its title and without a seek bar, only behind this scheme.
    /// </summary>
    internal static string ToStreamUri(string uri) =>
        "x-rincon-mp3radio" + uri[uri.IndexOf(':')..];

    internal static string CreateStreamMetadata(string title) =>
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        "<item id=\"R:0/0/0\" parentID=\"R:0/0\" restricted=\"true\">" +
        "<dc:title>" + SecurityElement.Escape(title) + "</dc:title>" +
        "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
        "</item></DIDL-Lite>";
}
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests`
Expected: all `SonosValuesTests` pass.

- [ ] **Step 10: Commit**

```bash
git add src/Directory.Packages.props src/Namotion.Interceptor.slnx src/HomeBlaze/Namotion.Devices.Sonos src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: scaffold the Sonos device library with wire value conversions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Zone topology parser

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/Parsing/SonosTopology.cs`, `Parsing/ZoneGroupStateParser.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Fixtures/zone-group-state.xml`, `Testing/TestFixtures.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/ZoneGroupStateParserTests.cs`

**Interfaces:**
- Consumes: `SonosSatelliteRole` (Task 2).
- Produces (namespace `Namotion.Devices.Sonos.Parsing`): `record SonosTopology(IReadOnlyList<SonosTopologyGroup> Groups)`, `record SonosTopologyGroup(string Id, string CoordinatorUuid, IReadOnlyList<SonosTopologyPlayer> Players)`, `record SonosTopologyPlayer(string Uuid, string RoomName, Uri BaseUri, string? SoftwareVersion, bool? IsWireless, string? MoreInfo, IReadOnlyList<SonosTopologySatellite> Satellites)`, `record SonosTopologySatellite(string Uuid, string RoomName, Uri BaseUri, string? SoftwareVersion, bool? IsWireless, SonosSatelliteRole Role)`, `static SonosTopology ZoneGroupStateParser.Parse(string xml)`. Test helper `TestFixtures.Read(string name)`.

- [ ] **Step 1: Add the fixture** (anonymized from the live household: UUIDs, IPs replaced)

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Fixtures/zone-group-state.xml`:

```xml
<ZoneGroupState>
  <ZoneGroups>
    <ZoneGroup Coordinator="RINCON_A0000000000101400" ID="RINCON_A0000000000101400:1010349259">
      <ZoneGroupMember UUID="RINCON_A0000000000101400" Location="http://10.0.0.101:1400/xml/device_description.xml" ZoneName="Wohnzimmer" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000101400:LF,RF;RINCON_A0000000000201400:SW;RINCON_A0000000000301400:LR,LTR;RINCON_A0000000000401400:RR,RTR" WirelessMode="0" EthLink="1" MoreInfo="">
        <Satellite UUID="RINCON_A0000000000401400" Location="http://10.0.0.120:1400/xml/device_description.xml" ZoneName="Wohnzimmer" Invisible="1" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000101400:LF,RF;RINCON_A0000000000401400:RR,RTR" WirelessMode="3" EthLink="0" MoreInfo="" />
        <Satellite UUID="RINCON_A0000000000201400" Location="http://10.0.0.108:1400/xml/device_description.xml" ZoneName="Sub" Invisible="1" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000101400:LF,RF;RINCON_A0000000000201400:SW" WirelessMode="0" EthLink="0" MoreInfo="" />
        <Satellite UUID="RINCON_A0000000000301400" Location="http://10.0.0.136:1400/xml/device_description.xml" ZoneName="Wohnzimmer" Invisible="1" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000101400:LF,RF;RINCON_A0000000000301400:LR,LTR" WirelessMode="3" EthLink="0" MoreInfo="" />
      </ZoneGroupMember>
    </ZoneGroup>
    <ZoneGroup Coordinator="RINCON_A0000000000501400" ID="RINCON_A0000000000501400:3894694580">
      <ZoneGroupMember UUID="RINCON_A0000000000501400" Location="http://10.0.0.137:1400/xml/device_description.xml" ZoneName="Terasse" SoftwareVersion="97.1-80312" WirelessMode="1" EthLink="0" MoreInfo="RawBattPct:100,BattPct:100,BattChg:CHARGING,BattTmp:23" />
    </ZoneGroup>
    <ZoneGroup Coordinator="RINCON_A0000000000601400" ID="RINCON_A0000000000601400:451005965">
      <ZoneGroupMember UUID="RINCON_A0000000000601400" Location="http://10.0.0.121:1400/xml/device_description.xml" ZoneName="Küche" SoftwareVersion="97.1-80312" WirelessMode="1" EthLink="0" MoreInfo="" />
    </ZoneGroup>
    <ZoneGroup Coordinator="RINCON_A0000000000701400" ID="RINCON_A0000000000701400:2697524507">
      <ZoneGroupMember UUID="RINCON_A0000000000701400" Location="http://10.0.0.116:1400/xml/device_description.xml" ZoneName="Büro" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000701400:LF,RF;RINCON_A0000000000801400:SW;RINCON_A0000000000901400:LR;RINCON_A0000000000A01400:RR" WirelessMode="0" EthLink="0" MoreInfo="">
        <Satellite UUID="RINCON_A0000000000901400" Location="http://10.0.0.118:1400/xml/device_description.xml" ZoneName="Büro" Invisible="1" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000701400:LF,RF;RINCON_A0000000000901400:LR" WirelessMode="0" EthLink="0" MoreInfo="" />
        <Satellite UUID="RINCON_A0000000000A01400" Location="http://10.0.0.110:1400/xml/device_description.xml" ZoneName="Büro" Invisible="1" SoftwareVersion="97.1-80312" HTSatChanMapSet="RINCON_A0000000000701400:LF,RF;RINCON_A0000000000A01400:RR" WirelessMode="0" EthLink="0" MoreInfo="" />
        <Satellite UUID="RINCON_A0000000000801400" Location="http://10.0.0.242:1400/xml/device_description.xml" ZoneName="Sub" Invisible="1" SoftwareVersion="86.10-80260" HTSatChanMapSet="RINCON_A0000000000701400:LF,RF;RINCON_A0000000000801400:SW" WirelessMode="0" EthLink="0" MoreInfo="" />
      </ZoneGroupMember>
    </ZoneGroup>
  </ZoneGroups>
  <VanishedDevices />
</ZoneGroupState>
```

- [ ] **Step 2: Add the fixture reader**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/TestFixtures.cs`:

```csharp
namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class TestFixtures
{
    internal const string LivingRoomUuid = "RINCON_A0000000000101400";
    internal const string TerraceUuid = "RINCON_A0000000000501400";
    internal const string KitchenUuid = "RINCON_A0000000000601400";
    internal const string OfficeUuid = "RINCON_A0000000000701400";

    internal static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
```

- [ ] **Step 3: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/ZoneGroupStateParserTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class ZoneGroupStateParserTests
{
    private static SonosTopology ParseHousehold() =>
        ZoneGroupStateParser.Parse(TestFixtures.Read("zone-group-state.xml"));

    [Fact]
    public void WhenParsingHousehold_ThenEachGroupHasItsVisiblePlayer()
    {
        // Act
        var topology = ParseHousehold();

        // Assert
        Assert.Equal(4, topology.Groups.Count);
        Assert.All(topology.Groups, group => Assert.Single(group.Players));
        Assert.Equal(
            new[] { "Wohnzimmer", "Terasse", "Küche", "Büro" },
            topology.Groups.Select(group => group.Players[0].RoomName));
        Assert.Equal(TestFixtures.LivingRoomUuid, topology.Groups[0].CoordinatorUuid);
        Assert.Equal("RINCON_A0000000000101400:1010349259", topology.Groups[0].Id);
    }

    [Fact]
    public void WhenParsingHomeTheater_ThenSatelliteRolesComeFromTheChannelMap()
    {
        // Act
        var livingRoom = ParseHousehold().Groups[0].Players[0];

        // Assert
        Assert.Equal(3, livingRoom.Satellites.Count);
        Assert.Equal(SonosSatelliteRole.Subwoofer, livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000201400").Role);
        Assert.Equal(SonosSatelliteRole.RearLeft, livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000301400").Role);
        Assert.Equal(SonosSatelliteRole.RearRight, livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000401400").Role);
        Assert.Equal(new Uri("http://10.0.0.120:1400/"), livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000401400").BaseUri);
    }

    [Fact]
    public void WhenParsingMember_ThenBaseUriVersionAndWirelessAreRead()
    {
        // Act
        var topology = ParseHousehold();
        var livingRoom = topology.Groups[0].Players[0];
        var kitchen = topology.Groups[2].Players[0];

        // Assert
        Assert.Equal(new Uri("http://10.0.0.101:1400/"), livingRoom.BaseUri);
        Assert.Equal("97.1-80312", livingRoom.SoftwareVersion);
        Assert.False(livingRoom.IsWireless);
        Assert.True(kitchen.IsWireless);
    }

    [Fact]
    public void WhenParsingPortable_ThenMoreInfoIsKept()
    {
        // Act
        var terrace = ParseHousehold().Groups[1].Players[0];

        // Assert
        Assert.Contains("BattPct:100", terrace.MoreInfo);
    }

    [Fact]
    public void WhenParsingStereoPair_ThenInvisibleMemberBecomesStereoPartner()
    {
        // Arrange
        const string xml = """
            <ZoneGroupState><ZoneGroups>
              <ZoneGroup Coordinator="RINCON_B0000000000101400" ID="RINCON_B0000000000101400:1">
                <ZoneGroupMember UUID="RINCON_B0000000000101400" Location="http://10.0.0.50:1400/xml/device_description.xml" ZoneName="Bad" ChannelMapSet="RINCON_B0000000000101400:LF,LF;RINCON_B0000000000201400:RF,RF" EthLink="0" />
                <ZoneGroupMember UUID="RINCON_B0000000000201400" Location="http://10.0.0.51:1400/xml/device_description.xml" ZoneName="Bad" Invisible="1" ChannelMapSet="RINCON_B0000000000101400:LF,LF;RINCON_B0000000000201400:RF,RF" EthLink="0" />
              </ZoneGroup>
            </ZoneGroups></ZoneGroupState>
            """;

        // Act
        var topology = ZoneGroupStateParser.Parse(xml);

        // Assert
        var player = Assert.Single(Assert.Single(topology.Groups).Players);
        var partner = Assert.Single(player.Satellites);
        Assert.Equal("RINCON_B0000000000201400", partner.Uuid);
        Assert.Equal(SonosSatelliteRole.StereoPartner, partner.Role);
    }

    [Fact]
    public void WhenRootIsZoneGroups_ThenItIsParsed()
    {
        // Arrange
        const string xml = """
            <ZoneGroups>
              <ZoneGroup Coordinator="RINCON_C0000000000101400" ID="RINCON_C0000000000101400:1">
                <ZoneGroupMember UUID="RINCON_C0000000000101400" Location="http://10.0.0.60:1400/xml/device_description.xml" ZoneName="Garage" />
              </ZoneGroup>
            </ZoneGroups>
            """;

        // Act
        var topology = ZoneGroupStateParser.Parse(xml);

        // Assert
        Assert.Equal("Garage", Assert.Single(Assert.Single(topology.Groups).Players).RoomName);
        Assert.Null(topology.Groups[0].Players[0].IsWireless);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~ZoneGroupStateParserTests"`
Expected: build fails, `ZoneGroupStateParser` does not exist.

- [ ] **Step 5: Implement the records**

`src/HomeBlaze/Namotion.Devices.Sonos/Parsing/SonosTopology.cs`:

```csharp
namespace Namotion.Devices.Sonos.Parsing;

internal sealed record SonosTopology(IReadOnlyList<SonosTopologyGroup> Groups);

internal sealed record SonosTopologyGroup(
    string Id,
    string CoordinatorUuid,
    IReadOnlyList<SonosTopologyPlayer> Players);

internal sealed record SonosTopologyPlayer(
    string Uuid,
    string RoomName,
    Uri BaseUri,
    string? SoftwareVersion,
    bool? IsWireless,
    string? MoreInfo,
    IReadOnlyList<SonosTopologySatellite> Satellites);

internal sealed record SonosTopologySatellite(
    string Uuid,
    string RoomName,
    Uri BaseUri,
    string? SoftwareVersion,
    bool? IsWireless,
    SonosSatelliteRole Role);
```

- [ ] **Step 6: Implement the parser**

`src/HomeBlaze/Namotion.Devices.Sonos/Parsing/ZoneGroupStateParser.cs`:

```csharp
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses the ZoneGroupState XML returned by GetZoneGroupState and sent in ZoneGroupTopology events.
/// </summary>
internal static class ZoneGroupStateParser
{
    internal static SonosTopology Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var groups = new List<SonosTopologyGroup>();
        foreach (var groupElement in document.Descendants("ZoneGroup"))
        {
            var members = groupElement.Elements("ZoneGroupMember").ToList();
            var players = new List<SonosTopologyPlayer>();
            foreach (var member in members)
            {
                if (IsInvisible(member))
                {
                    continue;
                }

                var uuid = (string)member.Attribute("UUID")!;
                var satellites = new List<SonosTopologySatellite>();

                var homeTheaterMap = (string?)member.Attribute("HTSatChanMapSet");
                foreach (var satellite in member.Elements("Satellite"))
                {
                    satellites.Add(CreateSatellite(satellite, homeTheaterMap, isStereoPair: false));
                }

                // A stereo pair lists its second speaker as an invisible member of the same group.
                var stereoMap = (string?)member.Attribute("ChannelMapSet");
                foreach (var partner in members)
                {
                    if (IsInvisible(partner) && ContainsUuid(stereoMap, (string)partner.Attribute("UUID")!))
                    {
                        satellites.Add(CreateSatellite(partner, stereoMap, isStereoPair: true));
                    }
                }

                players.Add(new SonosTopologyPlayer(
                    uuid,
                    (string?)member.Attribute("ZoneName") ?? string.Empty,
                    GetBaseUri(member),
                    (string?)member.Attribute("SoftwareVersion"),
                    GetIsWireless(member),
                    (string?)member.Attribute("MoreInfo"),
                    satellites));
            }

            if (players.Count > 0)
            {
                groups.Add(new SonosTopologyGroup(
                    (string?)groupElement.Attribute("ID") ?? string.Empty,
                    (string?)groupElement.Attribute("Coordinator") ?? players[0].Uuid,
                    players));
            }
        }

        return new SonosTopology(groups);
    }

    private static SonosTopologySatellite CreateSatellite(XElement element, string? channelMap, bool isStereoPair)
    {
        var uuid = (string)element.Attribute("UUID")!;
        return new SonosTopologySatellite(
            uuid,
            (string?)element.Attribute("ZoneName") ?? string.Empty,
            GetBaseUri(element),
            (string?)element.Attribute("SoftwareVersion"),
            GetIsWireless(element),
            GetRole(channelMap, uuid, isStereoPair));
    }

    private static bool IsInvisible(XElement element) =>
        (string?)element.Attribute("Invisible") == "1";

    private static Uri GetBaseUri(XElement element)
    {
        var location = new Uri((string)element.Attribute("Location")!);
        return new Uri(location.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool? GetIsWireless(XElement element) => (string?)element.Attribute("EthLink") switch
    {
        "1" => false,
        "0" => true,
        _ => null
    };

    private static bool ContainsUuid(string? channelMap, string uuid) =>
        channelMap is not null && FindChannels(channelMap, uuid) is not null;

    private static string[]? FindChannels(string channelMap, string uuid)
    {
        foreach (var entry in channelMap.Split(';'))
        {
            if (entry.Length > uuid.Length && entry[uuid.Length] == ':' && entry.StartsWith(uuid, StringComparison.Ordinal))
            {
                return entry[(uuid.Length + 1)..].Split(',');
            }
        }

        return null;
    }

    private static SonosSatelliteRole GetRole(string? channelMap, string uuid, bool isStereoPair)
    {
        var channels = channelMap is null ? null : FindChannels(channelMap, uuid);
        if (channels is not null)
        {
            if (channels.Contains("SW"))
            {
                return SonosSatelliteRole.Subwoofer;
            }

            if (channels.Contains("LR"))
            {
                return SonosSatelliteRole.RearLeft;
            }

            if (channels.Contains("RR"))
            {
                return SonosSatelliteRole.RearRight;
            }
        }

        return isStereoPair ? SonosSatelliteRole.StereoPartner : SonosSatelliteRole.Other;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~ZoneGroupStateParserTests"`
Expected: 6 passed.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos/Parsing src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: parse the Sonos zone topology including satellites and battery

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: UPnP event and DIDL metadata parsers

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/Parsing/ServiceChanges.cs`, `Parsing/UpnpEventParser.cs`, `Parsing/DidlParser.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/SonosEventBodies.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/UpnpEventParserTests.cs`, `DidlParserTests.cs`

**Interfaces:**
- Consumes: `SonosValues.IsKnown`, `SonosValues.NullIfEmpty` (Task 2).
- Produces (namespace `Namotion.Devices.Sonos.Parsing`): `record AvTransportChange(string? TransportState, string? PlayMode, string? MediaUri, string? TrackUri, string? TrackDuration, string? TrackMetaData)`, `record RenderingControlChange(int? Volume, bool? Mute, int? Bass, int? Treble, bool? Loudness, bool? NightMode, bool? SpeechEnhancement)`, `record GroupRenderingControlChange(int? Volume, bool? Mute)`, `record SonosPlayerReading(AvTransportChange AvTransport, TimeSpan? Position, TimeSpan? SleepTimerRemaining, RenderingControlChange RenderingControl)`, `UpnpEventParser.ParseAvTransport(string body)`, `ParseRenderingControl(string body)`, `ParseGroupRenderingControl(string body)`, `ParseZoneGroupState(string body) : string?`, `record DidlTrack(string? Title, string? Artist, string? Album, string? AlbumArtUri)`, `DidlParser.ParseTrack(string? metadata) : DidlTrack?`. Test helper `SonosEventBodies.AvTransport(...)`, `.RenderingControl(...)`, `.Properties(...)`, `.Didl(...)`.

- [ ] **Step 1: Add the event body builder**

Builds NOTIFY bodies with `XElement` so every escaping level (LastChange text holding Event XML whose attributes hold DIDL XML) is produced correctly.

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/SonosEventBodies.cs`:

```csharp
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class SonosEventBodies
{
    private static readonly XNamespace EventNamespace = "urn:schemas-upnp-org:event-1-0";
    private static readonly XNamespace AvTransportNamespace = "urn:schemas-upnp-org:metadata-1-0/AVT/";
    private static readonly XNamespace RenderingControlNamespace = "urn:schemas-upnp-org:metadata-1-0/RCS/";
    private static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    internal static string AvTransport(params (string Name, string Value)[] values) =>
        LastChange(AvTransportNamespace, values.Select(value =>
            new XElement(AvTransportNamespace + value.Name, new XAttribute("val", value.Value))));

    internal static string RenderingControl(params (string Name, string? Channel, string Value)[] values) =>
        LastChange(RenderingControlNamespace, values.Select(value =>
            new XElement(RenderingControlNamespace + value.Name,
                value.Channel is null ? null : new XAttribute("channel", value.Channel),
                new XAttribute("val", value.Value))));

    internal static string Properties(params (string Name, string Value)[] properties) =>
        new XElement(EventNamespace + "propertyset",
                new XAttribute(XNamespace.Xmlns + "e", EventNamespace.NamespaceName),
                properties.Select(property =>
                    new XElement(EventNamespace + "property", new XElement(property.Name, property.Value))))
            .ToString(SaveOptions.DisableFormatting);

    internal static string Didl(
        string? title,
        string? artist = null,
        string? album = null,
        string? albumArtUri = null,
        string? streamContent = null) =>
        new XElement(DidlNamespace + "DIDL-Lite",
                new XAttribute(XNamespace.Xmlns + "dc", DcNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "upnp", UpnpNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", RinconNamespace.NamespaceName),
                new XElement(DidlNamespace + "item",
                    new XAttribute("id", "-1"),
                    new XAttribute("parentID", "-1"),
                    title is null ? null : new XElement(DcNamespace + "title", title),
                    artist is null ? null : new XElement(DcNamespace + "creator", artist),
                    album is null ? null : new XElement(UpnpNamespace + "album", album),
                    albumArtUri is null ? null : new XElement(UpnpNamespace + "albumArtURI", albumArtUri),
                    streamContent is null ? null : new XElement(RinconNamespace + "streamContent", streamContent),
                    new XElement(UpnpNamespace + "class", "object.item.audioItem.musicTrack")))
            .ToString(SaveOptions.DisableFormatting);

    private static string LastChange(XNamespace serviceNamespace, IEnumerable<XElement> values)
    {
        var lastChange = new XElement(serviceNamespace + "Event",
                new XElement(serviceNamespace + "InstanceID", new XAttribute("val", "0"), values))
            .ToString(SaveOptions.DisableFormatting);

        return Properties(("LastChange", lastChange));
    }
}
```

- [ ] **Step 2: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/UpnpEventParserTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class UpnpEventParserTests
{
    private const string SpotifyUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf";

    [Fact]
    public void WhenAvTransportEventHasSpotifyTrack_ThenAllFieldsAreRead()
    {
        // Arrange
        var body = SonosEventBodies.AvTransport(
            ("TransportState", "PLAYING"),
            ("CurrentPlayMode", "SHUFFLE_NOREPEAT"),
            ("CurrentTrackURI", SpotifyUri),
            ("CurrentTrackDuration", "0:03:25"),
            ("CurrentTrackMetaData", SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x")),
            ("AVTransportURI", SpotifyUri));

        // Act
        var change = UpnpEventParser.ParseAvTransport(body);

        // Assert
        Assert.Equal("PLAYING", change.TransportState);
        Assert.Equal("SHUFFLE_NOREPEAT", change.PlayMode);
        Assert.Equal(SpotifyUri, change.TrackUri);
        Assert.Equal(SpotifyUri, change.MediaUri);
        Assert.Equal("0:03:25", change.TrackDuration);
        Assert.Contains("<dc:title>Song</dc:title>", change.TrackMetaData);
    }

    [Fact]
    public void WhenAvTransportEventOmitsFields_ThenTheyAreNull()
    {
        // Arrange
        var body = SonosEventBodies.AvTransport(("TransportState", "STOPPED"));

        // Act
        var change = UpnpEventParser.ParseAvTransport(body);

        // Assert
        Assert.Equal("STOPPED", change.TransportState);
        Assert.Null(change.PlayMode);
        Assert.Null(change.TrackUri);
        Assert.Null(change.TrackMetaData);
    }

    [Fact]
    public void WhenRenderingControlEvent_ThenOnlyMasterChannelValuesAreRead()
    {
        // Arrange
        var body = SonosEventBodies.RenderingControl(
            ("Volume", "LF", "100"),
            ("Volume", "Master", "22"),
            ("Mute", "Master", "1"),
            ("Bass", null, "-2"),
            ("Treble", null, "3"),
            ("Loudness", "Master", "1"),
            ("NightMode", null, "1"),
            ("DialogLevel", null, "0"));

        // Act
        var change = UpnpEventParser.ParseRenderingControl(body);

        // Assert
        Assert.Equal(new RenderingControlChange(22, true, -2, 3, true, true, false), change);
    }

    [Fact]
    public void WhenGroupRenderingControlEvent_ThenGroupVolumeAndMuteAreRead()
    {
        // Arrange
        var body = SonosEventBodies.Properties(("GroupMute", "0"), ("GroupVolume", "35"), ("GroupVolumeChangeable", "1"));

        // Act
        var change = UpnpEventParser.ParseGroupRenderingControl(body);

        // Assert
        Assert.Equal(new GroupRenderingControlChange(35, false), change);
    }

    [Fact]
    public void WhenTopologyEvent_ThenZoneGroupStateIsReturned()
    {
        // Arrange
        var zoneGroupState = TestFixtures.Read("zone-group-state.xml");
        var body = SonosEventBodies.Properties(("ZoneGroupState", zoneGroupState), ("ThirdPartyMediaServersX", "x"));

        // Act
        var result = UpnpEventParser.ParseZoneGroupState(body);

        // Assert
        Assert.Equal(zoneGroupState, result);
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/DidlParserTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class DidlParserTests
{
    [Fact]
    public void WhenMetadataHasTrack_ThenTitleArtistAlbumAndArtAreRead()
    {
        // Arrange
        var metadata = SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x");

        // Act
        var track = DidlParser.ParseTrack(metadata);

        // Assert
        Assert.Equal(new DidlTrack("Song", "Artist", "Album", "/getaa?s=1&u=x"), track);
    }

    [Fact]
    public void WhenMetadataHasStreamContent_ThenStreamContentIsTheTitle()
    {
        // Arrange
        var metadata = SonosEventBodies.Didl("x-sonosapi-stream:tunein", streamContent: "Artist - Live Song");

        // Act
        var track = DidlParser.ParseTrack(metadata);

        // Assert
        Assert.Equal("Artist - Live Song", track?.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("<not-xml")]
    [InlineData("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" />")]
    public void WhenMetadataIsMissingOrInvalid_ThenReturnsNull(string? metadata)
    {
        // Act
        var track = DidlParser.ParseTrack(metadata);

        // Assert
        Assert.Null(track);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~UpnpEventParserTests|FullyQualifiedName~DidlParserTests"`
Expected: build fails, `UpnpEventParser`, `DidlParser` and the change records do not exist.

- [ ] **Step 4: Implement the change records**

`src/HomeBlaze/Namotion.Devices.Sonos/Parsing/ServiceChanges.cs`:

```csharp
namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// AVTransport values from an event or a poll. A null field was not reported and keeps the current value.
/// </summary>
internal sealed record AvTransportChange(
    string? TransportState,
    string? PlayMode,
    string? MediaUri,
    string? TrackUri,
    string? TrackDuration,
    string? TrackMetaData);

/// <summary>
/// RenderingControl values on the Master channel. A null field was not reported and keeps the current value.
/// </summary>
internal sealed record RenderingControlChange(
    int? Volume,
    bool? Mute,
    int? Bass,
    int? Treble,
    bool? Loudness,
    bool? NightMode,
    bool? SpeechEnhancement);

/// <summary>
/// GroupRenderingControl values read from a group coordinator.
/// </summary>
internal sealed record GroupRenderingControlChange(int? Volume, bool? Mute);

/// <summary>
/// One poll of a player. Position and sleep timer are never evented, so they come only from here.
/// </summary>
internal sealed record SonosPlayerReading(
    AvTransportChange AvTransport,
    TimeSpan? Position,
    TimeSpan? SleepTimerRemaining,
    RenderingControlChange RenderingControl);
```

- [ ] **Step 5: Implement the event parser**

`src/HomeBlaze/Namotion.Devices.Sonos/Parsing/UpnpEventParser.cs`:

```csharp
using System.Globalization;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses UPnP NOTIFY bodies (<c>e:propertyset</c>) from Sonos services.
/// </summary>
internal static class UpnpEventParser
{
    private static readonly XNamespace EventNamespace = "urn:schemas-upnp-org:event-1-0";

    internal static AvTransportChange ParseAvTransport(string body)
    {
        var values = ParseLastChange(body);
        return new AvTransportChange(
            values.GetValueOrDefault("TransportState"),
            values.GetValueOrDefault("CurrentPlayMode"),
            values.GetValueOrDefault("AVTransportURI"),
            values.GetValueOrDefault("CurrentTrackURI"),
            values.GetValueOrDefault("CurrentTrackDuration"),
            values.GetValueOrDefault("CurrentTrackMetaData"));
    }

    internal static RenderingControlChange ParseRenderingControl(string body)
    {
        var values = ParseLastChange(body);
        return new RenderingControlChange(
            GetInt(values, "Volume"),
            GetBool(values, "Mute"),
            GetInt(values, "Bass"),
            GetInt(values, "Treble"),
            GetBool(values, "Loudness"),
            GetBool(values, "NightMode"),
            GetBool(values, "DialogLevel"));
    }

    internal static GroupRenderingControlChange ParseGroupRenderingControl(string body)
    {
        var properties = ParseProperties(body);
        return new GroupRenderingControlChange(GetInt(properties, "GroupVolume"), GetBool(properties, "GroupMute"));
    }

    internal static string? ParseZoneGroupState(string body) =>
        ParseProperties(body).GetValueOrDefault("ZoneGroupState");

    private static Dictionary<string, string> ParseProperties(string body)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = XDocument.Parse(body).Root;
        if (root is null)
        {
            return properties;
        }

        foreach (var property in root.Elements(EventNamespace + "property"))
        {
            foreach (var element in property.Elements())
            {
                properties[element.Name.LocalName] = element.Value;
            }
        }

        return properties;
    }

    /// <summary>
    /// Reads the <c>LastChange</c> property, whose text is an Event document with one element per changed
    /// value. Values on channels other than Master (per-speaker LF/RF trims) are ignored.
    /// </summary>
    private static Dictionary<string, string> ParseLastChange(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ParseProperties(body).TryGetValue("LastChange", out var lastChange) || lastChange.Length == 0)
        {
            return values;
        }

        var instance = XDocument.Parse(lastChange).Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "InstanceID");
        if (instance is null)
        {
            return values;
        }

        foreach (var element in instance.Elements())
        {
            var channel = (string?)element.Attribute("channel");
            var value = (string?)element.Attribute("val");
            if (value is not null && (channel is null || channel == "Master"))
            {
                values[element.Name.LocalName] = value;
            }
        }

        return values;
    }

    private static int? GetInt(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static bool? GetBool(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value == "1" : null;
}
```

- [ ] **Step 6: Implement the DIDL parser**

`src/HomeBlaze/Namotion.Devices.Sonos/Parsing/DidlParser.cs`:

```csharp
using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

internal sealed record DidlTrack(string? Title, string? Artist, string? Album, string? AlbumArtUri);

/// <summary>
/// Reads track metadata from DIDL-Lite documents.
/// </summary>
internal static class DidlParser
{
    private static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    /// <summary>
    /// Returns null for absent, <c>NOT_IMPLEMENTED</c> or malformed metadata: it comes from third-party music
    /// services, and a track without readable metadata is a normal state, not a failure.
    /// </summary>
    internal static DidlTrack? ParseTrack(string? metadata)
    {
        if (!SonosValues.IsKnown(metadata) || metadata.Length == 0)
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(metadata);
        }
        catch (XmlException)
        {
            return null;
        }

        var item = document.Root?.Element(DidlNamespace + "item");
        if (item is null)
        {
            return null;
        }

        // Radio stations put the current song into streamContent and the station into title.
        var streamContent = SonosValues.NullIfEmpty((string?)item.Element(RinconNamespace + "streamContent"));
        return new DidlTrack(
            streamContent ?? SonosValues.NullIfEmpty((string?)item.Element(DcNamespace + "title")),
            SonosValues.NullIfEmpty((string?)item.Element(DcNamespace + "creator")),
            SonosValues.NullIfEmpty((string?)item.Element(UpnpNamespace + "album")),
            SonosValues.NullIfEmpty((string?)item.Element(UpnpNamespace + "albumArtURI")));
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~UpnpEventParserTests|FullyQualifiedName~DidlParserTests"`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos/Parsing src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: parse Sonos UPnP events and DIDL track metadata

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Favorites and device description parsers

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/Parsing/FavoritesParser.cs`, `Parsing/DeviceDescriptionParser.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Fixtures/favorites.xml`, `Fixtures/device-description-ray.xml`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/FavoritesParserTests.cs`, `DeviceDescriptionParserTests.cs`

**Interfaces:**
- Produces: `record SonosFavorite(string Title, string Uri, string Metadata, bool IsContainer)`, `FavoritesParser.Parse(string result) : IReadOnlyList<SonosFavorite>`, `record SonosDeviceDescription(string? ModelName, string? ModelNumber, IReadOnlySet<string> ServiceIds)`, `DeviceDescriptionParser.Parse(string xml) : SonosDeviceDescription`.

Decision recorded here: the live household has two "shortcut" favorites (Sonos Radio stations) whose `<res>` is empty. They can only be started through the Sonos local websocket favorites API, so the parser skips them and `Favorites` lists only playable ones. `Sonos.md` lists the websocket API as a follow-up.

- [ ] **Step 1: Add the favorites fixture** (live `Browse FV:2` result, service account tokens redacted)

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Fixtures/favorites.xml` (one line in the real response, wrapped here only at item boundaries):

```xml
<DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/" xmlns:r="urn:schemas-rinconnetworks-com:metadata-1-0/" xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
<item id="FV:2/1" parentID="FV:2" restricted="false"><dc:title>Discover Sonos Radio</dc:title><upnp:class>object.itemobject.item.sonos-favorite</upnp:class><r:ordinal>0</r:ordinal><res></res><r:type>shortcut</r:type><r:description>From Sonos Radio</r:description><r:resMD>&lt;DIDL-Lite xmlns:dc=&quot;http://purl.org/dc/elements/1.1/&quot; xmlns:upnp=&quot;urn:schemas-upnp-org:metadata-1-0/upnp/&quot; xmlns:r=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot; xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;&gt;&lt;item id=&quot;10fe2064%2fstations%2fen-US%2fCH%2fc2Q6Q0g6ZGlzY292ZXItc29ub3MtcmFkaW8&quot; parentID=&quot;00080004%2fstations%2fen-US%2fCH%2fc2Q6Q0g6ZGlzY292ZXItc29ub3MtcmFkaW8&quot; restricted=&quot;true&quot;&gt;&lt;dc:title&gt;&lt;/dc:title&gt;&lt;upnp:class&gt;object.container&lt;/upnp:class&gt;&lt;desc id=&quot;cdudn&quot; nameSpace=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot;&gt;SA_RINCON77575_X_#Svc0-0-Token&lt;/desc&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</r:resMD></item>
<item id="FV:2/3" parentID="FV:2" restricted="false"><dc:title>Radio FM1</dc:title><upnp:class>object.itemobject.item.sonos-favorite</upnp:class><r:ordinal>1</r:ordinal><res protocolInfo="x-sonosapi-stream:*:*:*">x-sonosapi-stream:tunein%3a9557?sid=303&amp;flags=8232&amp;sn=1</res><upnp:albumArtURI>https://sali.sonos.radio/image?w=60&amp;image=https%3A%2F%2Fcdn-profiles.tunein.com%2Fs25077%2Fimages%2Flogog.jpg&amp;partnerId=tunein</upnp:albumArtURI><r:type>instantPlay</r:type><r:description>Sonos Radio Station</r:description><r:resMD>&lt;DIDL-Lite xmlns:dc=&quot;http://purl.org/dc/elements/1.1/&quot; xmlns:upnp=&quot;urn:schemas-upnp-org:metadata-1-0/upnp/&quot; xmlns:r=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot; xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;&gt;&lt;item id=&quot;10092028tunein%3a9557&quot; parentID=&quot;0009002ctunein%3a9557&quot; restricted=&quot;true&quot;&gt;&lt;dc:title&gt;Radio FM1&lt;/dc:title&gt;&lt;upnp:class&gt;object.item.audioItem.audioBroadcast&lt;/upnp:class&gt;&lt;desc id=&quot;cdudn&quot; nameSpace=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot;&gt;SA_RINCON77575_X_#Svc0-0-Token&lt;/desc&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</r:resMD></item>
<item id="FV:2/4" parentID="FV:2" restricted="false"><dc:title>SRF 3</dc:title><upnp:class>object.itemobject.item.sonos-favorite</upnp:class><r:ordinal>2</r:ordinal><res protocolInfo="x-sonosapi-stream:*:*:*">x-sonosapi-stream:tunein%3a9464?sid=303&amp;flags=8232&amp;sn=1</res><r:type>instantPlay</r:type><r:description>Sonos Radio Station</r:description><r:resMD>&lt;DIDL-Lite xmlns:dc=&quot;http://purl.org/dc/elements/1.1/&quot; xmlns:upnp=&quot;urn:schemas-upnp-org:metadata-1-0/upnp/&quot; xmlns:r=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot; xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;&gt;&lt;item id=&quot;10092028tunein%3a9464&quot; parentID=&quot;0009002ctunein%3a9464&quot; restricted=&quot;true&quot;&gt;&lt;dc:title&gt;SRF 3&lt;/dc:title&gt;&lt;upnp:class&gt;object.item.audioItem.audioBroadcast&lt;/upnp:class&gt;&lt;desc id=&quot;cdudn&quot; nameSpace=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot;&gt;SA_RINCON77575_X_#Svc0-0-Token&lt;/desc&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</r:resMD></item>
<item id="FV:2/0" parentID="FV:2" restricted="false"><dc:title>Trending in Germany</dc:title><upnp:class>object.itemobject.item.sonos-favorite</upnp:class><r:ordinal>3</r:ordinal><res></res><r:type>shortcut</r:type><r:description>From Sonos Radio</r:description><r:resMD>&lt;DIDL-Lite xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;&gt;&lt;/DIDL-Lite&gt;</r:resMD></item>
</DIDL-Lite>
```

- [ ] **Step 2: Add the device description fixture** (live Ray description, trimmed and anonymized)

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Fixtures/device-description-ray.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root xmlns="urn:schemas-upnp-org:device-1-0">
  <device>
    <deviceType>urn:schemas-upnp-org:device:ZonePlayer:1</deviceType>
    <manufacturer>Sonos, Inc.</manufacturer>
    <modelNumber>S36</modelNumber>
    <modelName>Sonos Ray</modelName>
    <UDN>uuid:RINCON_A0000000000701400</UDN>
    <roomName>Büro</roomName>
    <displayName>Ray</displayName>
    <serviceList>
      <service><serviceType>urn:schemas-upnp-org:service:AlarmClock:1</serviceType><serviceId>urn:upnp-org:serviceId:AlarmClock</serviceId></service>
      <service><serviceType>urn:schemas-upnp-org:service:DeviceProperties:1</serviceType><serviceId>urn:upnp-org:serviceId:DeviceProperties</serviceId></service>
      <service><serviceType>urn:schemas-upnp-org:service:ZoneGroupTopology:1</serviceType><serviceId>urn:upnp-org:serviceId:ZoneGroupTopology</serviceId></service>
      <service><serviceType>urn:schemas-upnp-org:service:GroupManagement:1</serviceType><serviceId>urn:upnp-org:serviceId:GroupManagement</serviceId></service>
      <service><serviceType>urn:schemas-upnp-org:service:HTControl:1</serviceType><serviceId>urn:upnp-org:serviceId:HTControl</serviceId></service>
    </serviceList>
    <deviceList>
      <device>
        <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
        <modelNumber>S36</modelNumber>
        <modelName>Sonos Ray</modelName>
        <serviceList>
          <service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId></service>
        </serviceList>
      </device>
      <device>
        <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
        <modelNumber>S36</modelNumber>
        <modelName>Sonos Ray</modelName>
        <serviceList>
          <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId></service>
          <service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><serviceId>urn:upnp-org:serviceId:AVTransport</serviceId></service>
          <service><serviceType>urn:schemas-sonos-com:service:Queue:1</serviceType><serviceId>urn:sonos-com:serviceId:Queue</serviceId></service>
          <service><serviceType>urn:schemas-upnp-org:service:GroupRenderingControl:1</serviceType><serviceId>urn:upnp-org:serviceId:GroupRenderingControl</serviceId></service>
        </serviceList>
      </device>
    </deviceList>
  </device>
</root>
```

- [ ] **Step 3: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/FavoritesParserTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class FavoritesParserTests
{
    [Fact]
    public void WhenParsingFavorites_ThenShortcutsWithoutUriAreSkipped()
    {
        // Act
        var favorites = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"));

        // Assert
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, favorites.Select(favorite => favorite.Title));
    }

    [Fact]
    public void WhenParsingStreamFavorite_ThenUriAndStoredMetadataAreKept()
    {
        // Act
        var radio = FavoritesParser.Parse(TestFixtures.Read("favorites.xml")).Single(favorite => favorite.Title == "Radio FM1");

        // Assert
        Assert.Equal("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", radio.Uri);
        Assert.Contains("<dc:title>Radio FM1</dc:title>", radio.Metadata);
        Assert.False(radio.IsContainer);
    }

    [Fact]
    public void WhenFavoriteIsContainer_ThenIsContainerIsTrue()
    {
        // Arrange
        const string result = """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:r="urn:schemas-rinconnetworks-com:metadata-1-0/" xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <item id="FV:2/9" parentID="FV:2"><dc:title>Road Trip</dc:title><res>x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a1?sid=9&amp;flags=8300&amp;sn=1</res><r:resMD>&lt;DIDL-Lite/&gt;</r:resMD></item>
            </DIDL-Lite>
            """;

        // Act
        var favorite = Assert.Single(FavoritesParser.Parse(result));

        // Assert
        Assert.True(favorite.IsContainer);
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/DeviceDescriptionParserTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class DeviceDescriptionParserTests
{
    [Fact]
    public void WhenParsingRayDescription_ThenModelAndServicesAreRead()
    {
        // Act
        var description = DeviceDescriptionParser.Parse(TestFixtures.Read("device-description-ray.xml"));

        // Assert
        Assert.Equal("Sonos Ray", description.ModelName);
        Assert.Equal("S36", description.ModelNumber);
        Assert.Contains("HTControl", description.ServiceIds);
        Assert.Contains("AVTransport", description.ServiceIds);
        Assert.Contains("GroupRenderingControl", description.ServiceIds);
        Assert.DoesNotContain("AudioIn", description.ServiceIds);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~FavoritesParserTests|FullyQualifiedName~DeviceDescriptionParserTests"`
Expected: build fails, parsers do not exist.

- [ ] **Step 5: Implement `FavoritesParser.cs`**

```csharp
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// A Sonos favorite that can be started over UPnP, with the metadata Sonos stored for it.
/// </summary>
internal sealed record SonosFavorite(string Title, string Uri, string Metadata, bool IsContainer);

/// <summary>
/// Parses the DIDL-Lite result of <c>Browse("FV:2")</c>.
/// </summary>
internal static class FavoritesParser
{
    private static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    internal static IReadOnlyList<SonosFavorite> Parse(string result)
    {
        var favorites = new List<SonosFavorite>();
        var root = XDocument.Parse(result).Root;
        if (root is null)
        {
            return favorites;
        }

        foreach (var item in root.Elements(DidlNamespace + "item"))
        {
            var title = (string?)item.Element(DcNamespace + "title");
            var uri = (string?)item.Element(DidlNamespace + "res");

            // Shortcut favorites (Sonos Radio stations) carry no URI and can only be started through the
            // Sonos websocket API, so they are not offered.
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(uri))
            {
                continue;
            }

            var metadata = (string?)item.Element(RinconNamespace + "resMD") ?? string.Empty;
            favorites.Add(new SonosFavorite(title, uri, metadata, IsContainer(uri, metadata)));
        }

        return favorites;
    }

    // Playlists and albums must go through the queue; SetAVTransportURI only accepts a single stream or track.
    private static bool IsContainer(string uri, string metadata) =>
        uri.StartsWith("x-rincon-cpcontainer:", StringComparison.Ordinal) ||
        metadata.Contains("<upnp:class>object.container", StringComparison.Ordinal);
}
```

- [ ] **Step 6: Implement `DeviceDescriptionParser.cs`**

```csharp
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// The parts of <c>/xml/device_description.xml</c> the subjects use.
/// </summary>
internal sealed record SonosDeviceDescription(string? ModelName, string? ModelNumber, IReadOnlySet<string> ServiceIds);

internal static class DeviceDescriptionParser
{
    private static readonly XNamespace DeviceNamespace = "urn:schemas-upnp-org:device-1-0";

    internal static SonosDeviceDescription Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var device = document.Root?.Element(DeviceNamespace + "device");

        // Service ids look like "urn:upnp-org:serviceId:AVTransport"; nested devices hold the media services.
        var serviceIds = document.Descendants(DeviceNamespace + "serviceId")
            .Select(element => element.Value[(element.Value.LastIndexOf(':') + 1)..])
            .ToHashSet(StringComparer.Ordinal);

        return new SonosDeviceDescription(
            SonosValues.NullIfEmpty((string?)device?.Element(DeviceNamespace + "modelName")),
            SonosValues.NullIfEmpty((string?)device?.Element(DeviceNamespace + "modelNumber")),
            serviceIds);
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~FavoritesParserTests|FullyQualifiedName~DeviceDescriptionParserTests"`
Expected: 4 passed.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos/Parsing src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: parse Sonos favorites and device descriptions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: UPnP event listener

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/Events/SonosEventSubscription.cs`, `Events/SonosEventListener.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/LoopbackHttpServer.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosEventListenerTests.cs`

**Interfaces:**
- Produces (namespace `Namotion.Devices.Sonos.Events`):
  - `sealed class SonosEventSubscription { string Key; Uri EventUri; Action<string> Handler; string? Sid; DateTimeOffset RenewAt; }`
  - `sealed class SonosEventListener : IAsyncDisposable` with `SonosEventListener(HttpClient httpClient, ILogger logger)`, `bool IsListening`, `IReadOnlyCollection<SonosEventSubscription> Subscriptions`, `void Start(string callbackHost, int port, string listenHost = "+")`, `Task<SonosEventSubscription> SubscribeAsync(string key, Uri eventUri, Action<string> handler, CancellationToken)`, `Task<bool> RenewAsync(SonosEventSubscription, CancellationToken)`, `Task UnsubscribeAsync(SonosEventSubscription, CancellationToken)`, `Task UnsubscribeAllAsync(CancellationToken)`.
  - Callback URL format: `http://{callbackHost}:{port}/event/{key}`; keys look like `{uuid}/{service}`.
- Produces (tests): `LoopbackHttpServer(Func<HttpListenerContext, Task> handler)` with `int Port`, `Uri BaseUri`, `static int GetFreePort()`.

- [ ] **Step 1: Add the loopback server helper**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/LoopbackHttpServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A minimal HTTP server on 127.0.0.1 that hands each request to a handler and closes the response.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly Task _loop;

    internal LoopbackHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = RunAsync();
    }

    internal int Port { get; }

    internal Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    internal static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task RunAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await _handler(context);
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            Console.Error.WriteLine(exception);
        }
        finally
        {
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosEventListenerTests.cs`:

```csharp
using System.Collections.Specialized;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Events;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosEventListenerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenSubscribing_ThenSendsCallbackNotificationTypeAndTimeout()
    {
        // Arrange
        var headers = new TaskCompletionSource<NameValueCollection>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(context =>
        {
            headers.TrySetResult(context.Request.Headers);
            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");

        // Act
        var subscription = await listener.SubscribeAsync(
            "RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/MediaRenderer/AVTransport/Event"), _ => { }, CancellationToken.None);

        // Assert
        var received = await headers.Task.WaitAsync(WaitTimeout);
        Assert.Equal($"<http://127.0.0.1:{port}/event/RINCON_X/AVTransport>", received["CALLBACK"]);
        Assert.Equal("upnp:event", received["NT"]);
        Assert.Equal("Second-1800", received["TIMEOUT"]);
        Assert.Equal("uuid:sub-1", subscription.Sid);
        Assert.InRange(subscription.RenewAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task WhenNotifyCarriesKnownSid_ThenHandlerReceivesBody()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), body => received.TrySetResult(body), CancellationToken.None);

        // Act
        var status = await SendNotifyAsync(httpClient, $"http://127.0.0.1:{port}/event/RINCON_X/AVTransport", "uuid:sub-1", "<body />");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("<body />", await received.Task.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenNotifyCarriesUnknownSid_ThenReturnsPreconditionFailed()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context => RespondWithSid(context, "uuid:sub-1"));
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");
        var handlerCalls = 0;
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => handlerCalls++, CancellationToken.None);

        // Act
        var status = await SendNotifyAsync(httpClient, $"http://127.0.0.1:{port}/event/RINCON_X/AVTransport", "uuid:stale", "<body />");

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task WhenNotifyArrivesBeforeSubscribeResponse_ThenHandlerStillReceivesIt()
    {
        // Arrange
        using var httpClient = new HttpClient();
        var notifyStatus = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            // Sonos sends the initial full state before answering SUBSCRIBE.
            var callback = context.Request.Headers["CALLBACK"]!.Trim('<', '>');
            notifyStatus.TrySetResult(await SendNotifyAsync(httpClient, callback, "uuid:sub-1", "<initial />"));
            await RespondWithSid(context, "uuid:sub-1");
        });
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        var port = LoopbackHttpServer.GetFreePort();
        listener.Start("127.0.0.1", port, listenHost: "127.0.0.1");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), body => received.TrySetResult(body), CancellationToken.None);

        // Assert
        Assert.Equal(HttpStatusCode.OK, await notifyStatus.Task.WaitAsync(WaitTimeout));
        Assert.Equal("<initial />", await received.Task.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenUnsubscribing_ThenSendsUnsubscribeWithSidAndForgetsTheSubscription()
    {
        // Arrange
        var unsubscribeSid = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var speaker = new LoopbackHttpServer(context =>
        {
            if (context.Request.HttpMethod == "UNSUBSCRIBE")
            {
                unsubscribeSid.TrySetResult(context.Request.Headers["SID"]);
                return Task.CompletedTask;
            }

            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        listener.Start("127.0.0.1", LoopbackHttpServer.GetFreePort(), listenHost: "127.0.0.1");
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        await listener.UnsubscribeAllAsync(CancellationToken.None);

        // Assert
        Assert.Equal("uuid:sub-1", await unsubscribeSid.Task.WaitAsync(WaitTimeout));
        Assert.Empty(listener.Subscriptions);
        Assert.Equal("RINCON_X/AVTransport", subscription.Key);
    }

    [Fact]
    public async Task WhenRenewalIsRejected_ThenSubscriptionIsForgotten()
    {
        // Arrange
        await using var speaker = new LoopbackHttpServer(context =>
        {
            if (context.Request.Headers["SID"] is not null)
            {
                context.Response.StatusCode = 412;
                return Task.CompletedTask;
            }

            return RespondWithSid(context, "uuid:sub-1");
        });
        using var httpClient = new HttpClient();
        await using var listener = new SonosEventListener(httpClient, NullLogger.Instance);
        listener.Start("127.0.0.1", LoopbackHttpServer.GetFreePort(), listenHost: "127.0.0.1");
        var subscription = await listener.SubscribeAsync("RINCON_X/AVTransport", new Uri(speaker.BaseUri, "/Event"), _ => { }, CancellationToken.None);

        // Act
        var renewed = await listener.RenewAsync(subscription, CancellationToken.None);

        // Assert
        Assert.False(renewed);
        Assert.Empty(listener.Subscriptions);
    }

    private static Task RespondWithSid(HttpListenerContext context, string sid)
    {
        context.Response.Headers["SID"] = sid;
        context.Response.Headers["TIMEOUT"] = "Second-1800";
        context.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    private static async Task<HttpStatusCode> SendNotifyAsync(HttpClient httpClient, string callback, string sid, string body)
    {
        using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
        {
            Content = new StringContent(body)
        };
        request.Headers.TryAddWithoutValidation("SID", sid);
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
        using var response = await httpClient.SendAsync(request);
        return response.StatusCode;
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~SonosEventListenerTests"`
Expected: build fails, `SonosEventListener` does not exist.

- [ ] **Step 4: Implement the subscription**

`src/HomeBlaze/Namotion.Devices.Sonos/Events/SonosEventSubscription.cs`:

```csharp
namespace Namotion.Devices.Sonos.Events;

/// <summary>
/// One UPnP event subscription with a Sonos service.
/// </summary>
internal sealed class SonosEventSubscription
{
    private volatile string? _sid;

    internal SonosEventSubscription(string key, Uri eventUri, Action<string> handler)
    {
        Key = key;
        EventUri = eventUri;
        Handler = handler;
    }

    /// <summary>
    /// The callback path segment, <c>{uuid}/{service}</c>.
    /// </summary>
    internal string Key { get; }

    internal Uri EventUri { get; }

    internal Action<string> Handler { get; }

    /// <summary>
    /// The subscription id, null until the speaker answered the SUBSCRIBE request.
    /// </summary>
    internal string? Sid
    {
        get => _sid;
        set => _sid = value;
    }

    internal DateTimeOffset RenewAt { get; set; }
}
```

- [ ] **Step 5: Implement the listener**

`src/HomeBlaze/Namotion.Devices.Sonos/Events/SonosEventListener.cs`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Sonos.Events;

/// <summary>
/// Receives UPnP NOTIFY requests from Sonos speakers and manages the subscriptions that produce them.
/// </summary>
internal sealed class SonosEventListener : IAsyncDisposable
{
    private const string EventPathPrefix = "/event/";
    private static readonly TimeSpan DefaultSubscriptionLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsBySid = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private Task? _acceptLoop;
    private string? _callbackBaseUri;

    internal SonosEventListener(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    internal bool IsListening => _listener?.IsListening == true;

    internal IReadOnlyCollection<SonosEventSubscription> Subscriptions => _subscriptionsByKey.Values.ToArray();

    /// <summary>
    /// Starts listening. Throws <see cref="HttpListenerException"/> when the port is taken or cannot be bound.
    /// </summary>
    internal void Start(string callbackHost, int port, string listenHost = "+")
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{listenHost}:{port}/");
        listener.Start();

        _listener = listener;
        _callbackBaseUri = $"http://{callbackHost}:{port}{EventPathPrefix}";
        _acceptLoop = AcceptLoopAsync(listener);
    }

    internal async Task<SonosEventSubscription> SubscribeAsync(
        string key, Uri eventUri, Action<string> handler, CancellationToken cancellationToken)
    {
        var subscription = new SonosEventSubscription(key, eventUri, handler);

        // Registered before the request: Sonos sends the initial full-state NOTIFY right after accepting, often
        // before its SUBSCRIBE response arrives, so that NOTIFY can only be matched by its callback path.
        _subscriptionsByKey[key] = subscription;
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), eventUri);
            request.Headers.TryAddWithoutValidation("CALLBACK", $"<{_callbackBaseUri}{key}>");
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("TIMEOUT", "Second-1800");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var sid = GetHeader(response, "SID")
                ?? throw new InvalidOperationException($"The subscription to {eventUri} returned no SID.");
            subscription.RenewAt = DateTimeOffset.UtcNow + GetLifetime(response) / 2;
            _subscriptionsBySid[sid] = subscription;
            subscription.Sid = sid;
            return subscription;
        }
        catch
        {
            _subscriptionsByKey.TryRemove(new KeyValuePair<string, SonosEventSubscription>(key, subscription));
            throw;
        }
    }

    /// <summary>
    /// Renews a subscription. A rejected renewal forgets the subscription so the caller subscribes again.
    /// </summary>
    internal async Task<bool> RenewAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
        request.Headers.TryAddWithoutValidation("TIMEOUT", "Second-1800");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Forget(subscription);
            return false;
        }

        subscription.RenewAt = DateTimeOffset.UtcNow + GetLifetime(response) / 2;
        return true;
    }

    internal async Task UnsubscribeAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        Forget(subscription);
        if (subscription.Sid is null)
        {
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Best effort: an unreachable speaker drops the subscription itself once it expires.
    /// </summary>
    internal async Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptionsByKey.Values)
        {
            try
            {
                await UnsubscribeAsync(subscription, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                _logger.LogDebug(exception, "Unsubscribing Sonos events {Key} failed.", subscription.Key);
            }
        }
    }

    private void Forget(SonosEventSubscription subscription)
    {
        _subscriptionsByKey.TryRemove(new KeyValuePair<string, SonosEventSubscription>(subscription.Key, subscription));
        if (subscription.Sid is { } sid)
        {
            _subscriptionsBySid.TryRemove(new KeyValuePair<string, SonosEventSubscription>(sid, subscription));
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (request.HttpMethod != "NOTIFY")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var subscription = Resolve(request);
            if (subscription is null)
            {
                // 412 tells the speaker to drop a subscription we no longer hold.
                response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            try
            {
                subscription.Handler(body);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Applying the Sonos event {Key} failed.", subscription.Key);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Handling a Sonos event request failed.");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            response.Close();
        }
    }

    private SonosEventSubscription? Resolve(HttpListenerRequest request)
    {
        var sid = request.Headers["SID"];
        if (sid is not null && _subscriptionsBySid.TryGetValue(sid, out var bySid))
        {
            return bySid;
        }

        // Only a subscription still waiting for its SID accepts a NOTIFY by path; once it has one, a different
        // SID is a stale subscription from an earlier run.
        var path = request.Url?.AbsolutePath;
        if (path is not null &&
            path.StartsWith(EventPathPrefix, StringComparison.Ordinal) &&
            _subscriptionsByKey.TryGetValue(Uri.UnescapeDataString(path[EventPathPrefix.Length..]), out var byKey) &&
            byKey.Sid is null)
        {
            return byKey;
        }

        return null;
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static TimeSpan GetLifetime(HttpResponseMessage response)
    {
        const string prefix = "Second-";
        var timeout = GetHeader(response, "TIMEOUT");
        return timeout is not null &&
            timeout.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(timeout.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : DefaultSubscriptionLifetime;
    }

    public async ValueTask DisposeAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            listener.Stop();
            listener.Close();
            if (_acceptLoop is not null)
            {
                await _acceptLoop;
            }
        }

        _subscriptionsByKey.Clear();
        _subscriptionsBySid.Clear();
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~SonosEventListenerTests"`
Expected: 6 passed.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos/Events src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: receive Sonos UPnP events with an HttpListener

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Subjects and topology reconciliation

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/SonosDevice.cs`, `SonosSatellite.cs`, `SonosPlayer.cs`, `SonosGroup.cs`, `SonosSystem.cs`, `SonosSystem.Runtime.cs` (temporary runtime, replaced in Task 9)
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/TestHttpClientFactory.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosSystemTopologyTests.cs`, `SonosPlayerStateTests.cs`, `SonosGroupStateTests.cs`, `SonosDerivedTrackingTests.cs`

**Interfaces:**
- Consumes: parsers and records from Tasks 3 to 5, `SonosValues` (Task 2).
- Produces:
  - `SonosDevice` (public, `[InterceptorSubject]`): `Uuid`, `RoomName`, `Model`, `ProductCode`, `SerialNumber`, `HardwareRevision`, `MacAddress`, `IpAddress`, `IsWireless`, `SoftwareVersion`, `IsConnected` (derived from internal `IsInTopology && IsReachable`), `StatusMessage`, `Title`, `IconName`; internal `BaseUri`, `FirmwareBuild`, `ServiceIds`, `IsInTopology`, `IsReachable`, `NeedsStaticData`, `ApplyTopology(string roomName, Uri baseUri, string? firmwareBuild, bool? isWireless)`, `ApplyStaticData(SonosDeviceDescription description, string? serialNumber, string? macAddress, string? hardwareRevision, string? softwareVersion)`, `InvalidateStaticData()`, `MarkMissing()`, `ReportPollSucceeded()`, `bool ReportPollFailed(string message)`.
  - `SonosSatellite : SonosDevice`: `Role`.
  - `SonosPlayer : SonosDevice` (in this task `IAudioPlayerState, IMediaTrackState, IBatteryState`): state listed in the spec; internal `MediaUri`, `ApplyPlayerTopology(SonosTopologyPlayer, string coordinatorUuid)`, `ApplyAvTransportEvent(AvTransportChange, DateTimeOffset)`, `ApplyRenderingControlEvent(RenderingControlChange, DateTimeOffset)`, `ApplyPoll(SonosPlayerReading, DateTimeOffset pollStartedAt)`.
  - `SonosGroup` (in this task `IAudioPlayerState, IMediaTrackState, IVirtualSubject, ITitleProvider, IIconProvider`): `GroupId`, `Coordinator`, `Members`, `Volume`, `IsMuted`, derived playback and track state; internal `Update(string groupId, SonosPlayer[] members)`, `ApplyGroupRenderingControlEvent(GroupRenderingControlChange, DateTimeOffset)`, `ApplyGroupRenderingControlPoll(GroupRenderingControlChange, DateTimeOffset)`.
  - `SonosSystem(IHttpClientFactory, ILogger<SonosSystem>)`: configuration and state from the spec, internal `ApplyTopology(SonosTopology)`, `FindPlayer(string roomNameOrUuid)`, `FindFavorite(string name)`, `ArgumentException CreateUnknownRoomException(string value, string parameterName)`, internal `Favorites` backing list via `SetFavorites(IReadOnlyList<SonosFavorite>)`.

- [ ] **Step 1: Add the test HTTP client factory**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/TestHttpClientFactory.cs`:

```csharp
namespace Namotion.Devices.Sonos.Tests.Testing;

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
```

- [ ] **Step 2: Write the failing topology tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosSystemTopologyTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemTopologyTests
{
    internal static SonosSystem CreateSystem() =>
        new(new TestHttpClientFactory(), NullLogger<SonosSystem>.Instance);

    internal static SonosTopology ReadHousehold() =>
        ZoneGroupStateParser.Parse(TestFixtures.Read("zone-group-state.xml"));

    [Fact]
    public void WhenCreated_ThenHasDefaults()
    {
        // Act
        var system = CreateSystem();

        // Assert
        Assert.Null(system.SeedHost);
        Assert.Null(system.EventCallbackHost);
        Assert.Equal(6329, system.EventPort);
        Assert.Equal(TimeSpan.FromSeconds(30), system.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), system.RetryInterval);
        Assert.Empty(system.Players);
        Assert.Empty(system.Groups);
        Assert.Empty(system.Favorites);
        Assert.Equal(HomeBlaze.Abstractions.ServiceStatus.Stopped, system.Status);
    }

    [Fact]
    public void WhenTopologyApplied_ThenPlayersAreKeyedByUuid()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(
            new[] { TestFixtures.LivingRoomUuid, TestFixtures.TerraceUuid, TestFixtures.KitchenUuid, TestFixtures.OfficeUuid },
            system.Players.Keys);
        Assert.Equal("Küche", system.Players[TestFixtures.KitchenUuid].RoomName);
        Assert.Equal("10.0.0.121", system.Players[TestFixtures.KitchenUuid].IpAddress);
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsConnected);
    }

    [Fact]
    public void WhenTopologyApplied_ThenSatellitesAreNestedUnderTheirPlayer()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(3, system.Players[TestFixtures.LivingRoomUuid].Satellites.Count);
        Assert.Equal(3, system.Players[TestFixtures.OfficeUuid].Satellites.Count);
        Assert.Empty(system.Players[TestFixtures.KitchenUuid].Satellites);
        Assert.Equal(SonosSatelliteRole.Subwoofer, system.Players[TestFixtures.OfficeUuid].Satellites["RINCON_A0000000000801400"].Role);
    }

    [Fact]
    public void WhenTopologyAppliedTwice_ThenInstancesAreKept()
    {
        // Arrange
        var system = CreateSystem();
        system.ApplyTopology(ReadHousehold());
        var players = system.Players;
        var groups = system.Groups;
        var livingRoom = system.Players[TestFixtures.LivingRoomUuid];
        var subwoofer = livingRoom.Satellites["RINCON_A0000000000201400"];

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Same(players, system.Players);
        Assert.Same(groups, system.Groups);
        Assert.Same(livingRoom, system.Players[TestFixtures.LivingRoomUuid]);
        Assert.Same(subwoofer, livingRoom.Satellites["RINCON_A0000000000201400"]);
    }

    [Fact]
    public void WhenPlayerMissingFromTopology_ThenItStaysButIsOffline()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        var withoutKitchen = new SonosTopology(household.Groups.Where(group => group.CoordinatorUuid != TestFixtures.KitchenUuid).ToArray());

        // Act
        system.ApplyTopology(withoutKitchen);

        // Assert
        var kitchen = system.Players[TestFixtures.KitchenUuid];
        Assert.False(kitchen.IsConnected);
        Assert.False(system.Groups.ContainsKey(TestFixtures.KitchenUuid));
    }

    [Fact]
    public void WhenTopologyApplied_ThenGroupsAreKeyedByCoordinator()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(4, system.Groups.Count);
        var group = system.Groups[TestFixtures.LivingRoomUuid];
        Assert.Same(system.Players[TestFixtures.LivingRoomUuid], group.Coordinator);
        Assert.Equal("Wohnzimmer", group.Title);
        Assert.Equal("RINCON_A0000000000101400:1010349259", group.GroupId);
    }

    [Fact]
    public void WhenRoomJoinsGroup_ThenGroupMembersTitleAndCoordinatorUpdate()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        var kitchen = household.Groups.Single(group => group.CoordinatorUuid == TestFixtures.KitchenUuid).Players[0];
        var grouped = new SonosTopology(household.Groups
            .Where(group => group.CoordinatorUuid != TestFixtures.KitchenUuid)
            .Select(group => group.CoordinatorUuid == TestFixtures.LivingRoomUuid
                ? group with { Players = [.. group.Players, kitchen] }
                : group)
            .ToArray());

        // Act
        system.ApplyTopology(grouped);

        // Assert
        Assert.Equal(3, system.Groups.Count);
        var group = system.Groups[TestFixtures.LivingRoomUuid];
        Assert.Equal("Wohnzimmer + Küche", group.Title);
        Assert.Equal(TestFixtures.LivingRoomUuid, system.Players[TestFixtures.KitchenUuid].GroupCoordinatorUuid);
        Assert.False(system.Players[TestFixtures.KitchenUuid].IsGroupCoordinator);
        Assert.True(system.Players[TestFixtures.LivingRoomUuid].IsGroupCoordinator);
    }

    [Fact]
    public void WhenPortableInTopology_ThenBatteryIsRead()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(1m, system.Players[TestFixtures.TerraceUuid].BatteryLevel);
        Assert.True(system.Players[TestFixtures.TerraceUuid].IsCharging);
        Assert.Null(system.Players[TestFixtures.KitchenUuid].BatteryLevel);
    }

    [Theory]
    [InlineData("küche")]
    [InlineData("RINCON_A0000000000601400")]
    public void WhenFindingPlayerByRoomOrUuid_ThenReturnsIt(string value)
    {
        // Arrange
        var system = CreateSystem();
        system.ApplyTopology(ReadHousehold());

        // Act
        var player = system.FindPlayer(value);

        // Assert
        Assert.Same(system.Players[TestFixtures.KitchenUuid], player);
    }
}
```

- [ ] **Step 3: Write the failing player and group state tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosPlayerStateTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosPlayerStateTests
{
    private const string SpotifyUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf";
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SonosPlayer CreateKitchen()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        return system.Players[TestFixtures.KitchenUuid];
    }

    private static AvTransportChange SpotifyPlaying() => new(
        "PLAYING",
        "SHUFFLE_NOREPEAT",
        SpotifyUri,
        SpotifyUri,
        "0:03:25",
        SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x"));

    private static SonosPlayerReading Reading(AvTransportChange avTransport, RenderingControlChange? renderingControl = null) =>
        new(avTransport, TimeSpan.FromSeconds(42), null, renderingControl ?? new RenderingControlChange(null, null, null, null, null, null, null));

    [Fact]
    public void WhenAvTransportEventApplied_ThenTrackStateUpdates()
    {
        // Arrange
        var player = CreateKitchen();

        // Act
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.True(player.IsPlaying);
        Assert.True(player.Shuffle);
        Assert.Equal(SonosRepeatMode.Off, player.Repeat);
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal("Artist", player.CurrentTrackArtist);
        Assert.Equal("Album", player.CurrentTrackAlbum);
        Assert.Equal("http://10.0.0.121:1400/getaa?s=1&u=x", player.CurrentTrackImageUri);
        Assert.Equal(TimeSpan.FromSeconds(205), player.CurrentTrackDuration);
        Assert.Equal(SonosSource.SpotifyConnect, player.Source);
    }

    [Fact]
    public void WhenPollReportsNotImplementedMetadata_ThenEventMetadataIsKept()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("PLAYING", "SHUFFLE_NOREPEAT", SpotifyUri, SpotifyUri, "NOT_IMPLEMENTED", "NOT_IMPLEMENTED")), T0.AddSeconds(1));

        // Assert
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal(TimeSpan.FromSeconds(205), player.CurrentTrackDuration);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenPollStartedBeforeEvent_ThenPollDoesNotOverwriteTheEvent()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0.AddSeconds(1));

        // Act
        player.ApplyPoll(
            Reading(new AvTransportChange("STOPPED", "NORMAL", "", "", "", ""), new RenderingControlChange(10, false, 0, 0, true, null, null)),
            T0);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal(0.1m, player.Volume);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenPollStartedAfterEvent_ThenPollApplies()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("STOPPED", "NORMAL", "", "", "0:00:00", "")), T0.AddSeconds(1));

        // Assert
        Assert.Equal(SonosTransportState.Stopped, player.TransportState);
        Assert.False(player.IsPlaying);
        Assert.Null(player.CurrentTrackTitle);
        Assert.Null(player.CurrentTrackUri);
        Assert.Equal(SonosSource.None, player.Source);
    }

    [Fact]
    public void WhenRenderingControlEventApplied_ThenValuesAreConverted()
    {
        // Arrange
        var player = CreateKitchen();

        // Act
        player.ApplyRenderingControlEvent(new RenderingControlChange(44, true, -2, 3, false, null, null), T0);

        // Assert
        Assert.Equal(0.44m, player.Volume);
        Assert.True(player.IsMuted);
        Assert.Equal(-2, player.Bass);
        Assert.Equal(3, player.Treble);
        Assert.False(player.Loudness);
        Assert.Null(player.NightMode);
    }

    [Fact]
    public void WhenRenderingControlPollStartedBeforeEvent_ThenVolumeFromEventIsKept()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyRenderingControlEvent(new RenderingControlChange(44, null, null, null, null, null, null), T0.AddSeconds(1));

        // Act
        player.ApplyPoll(Reading(new AvTransportChange(null, null, null, null, null, null), new RenderingControlChange(10, false, 0, 0, true, null, null)), T0);

        // Assert
        Assert.Equal(0.44m, player.Volume);
    }

    [Fact]
    public void WhenTvIsPlaying_ThenSourceIsTv()
    {
        // Arrange
        var player = CreateKitchen();
        const string tvUri = "x-sonos-htastream:RINCON_A0000000000601400:spdif";

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", null, tvUri, tvUri, null, null), T0);

        // Assert
        Assert.Equal(SonosSource.Tv, player.Source);
    }

    [Fact]
    public void WhenSleepTimerPolled_ThenRemainingTimeIsSet()
    {
        // Arrange
        var player = CreateKitchen();

        // Act
        player.ApplyPoll(new SonosPlayerReading(
            new AvTransportChange(null, null, null, null, null, null),
            null,
            TimeSpan.FromMinutes(30),
            new RenderingControlChange(null, null, null, null, null, null, null)), T0);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(30), player.SleepTimerRemaining);
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosGroupStateTests.cs`:

```csharp
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SonosSystem CreateSystem()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        return system;
    }

    [Fact]
    public void WhenGroupRenderingControlEventApplied_ThenVolumeIsAFraction()
    {
        // Arrange
        var group = CreateSystem().Groups[TestFixtures.LivingRoomUuid];

        // Act
        group.ApplyGroupRenderingControlEvent(new GroupRenderingControlChange(35, true), T0);

        // Assert
        Assert.Equal(0.35m, group.Volume);
        Assert.True(group.IsMuted);
    }

    [Fact]
    public void WhenGroupPollStartedBeforeEvent_ThenEventIsKept()
    {
        // Arrange
        var group = CreateSystem().Groups[TestFixtures.LivingRoomUuid];
        group.ApplyGroupRenderingControlEvent(new GroupRenderingControlChange(35, false), T0.AddSeconds(1));

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, false), T0);

        // Assert
        Assert.Equal(0.35m, group.Volume);
    }

    [Fact]
    public void WhenCoordinatorPlays_ThenGroupReportsCoordinatorTrack()
    {
        // Arrange
        var system = CreateSystem();
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Act
        system.Players[TestFixtures.LivingRoomUuid].ApplyAvTransportEvent(
            new AvTransportChange("PLAYING", null, null, "x-sonos-htastream:x:spdif", null, SonosEventBodies.Didl("Song", "Artist")),
            T0);

        // Assert
        Assert.True(group.IsPlaying);
        Assert.Equal("Song", group.CurrentTrackTitle);
        Assert.Equal("Artist", group.CurrentTrackArtist);
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosDerivedTrackingTests.cs`:

```csharp
using System.ComponentModel;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// Derived state must raise change notifications, otherwise it renders stale and is never recorded to history.
/// </summary>
public class SonosDerivedTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenTransportStateChanges_ThenIsPlayingRaisesPropertyChanged()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        var player = Track(system.Players[TestFixtures.KitchenUuid]);
        var firedEvents = TrackPropertyChanged(player);

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", null, null, null, null, null), T0);

        // Assert
        Assert.True(player.IsPlaying);
        Assert.Contains(nameof(SonosPlayer.IsPlaying), firedEvents);
    }

    [Fact]
    public void WhenCoordinatorTrackChanges_ThenGroupTitleOfTrackRaisesPropertyChanged()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        var coordinator = Track(system.Players[TestFixtures.LivingRoomUuid]);
        var group = Track(system.Groups[TestFixtures.LivingRoomUuid]);
        Assert.Null(group.CurrentTrackTitle);
        var firedEvents = TrackPropertyChanged(group);

        // Act
        coordinator.ApplyAvTransportEvent(new AvTransportChange(null, null, null, null, null, SonosEventBodies.Didl("Song")), T0);

        // Assert
        Assert.Equal("Song", group.CurrentTrackTitle);
        Assert.Contains(nameof(SonosGroup.CurrentTrackTitle), firedEvents);
    }

    private static T Track<T>(T subject) where T : IInterceptorSubject
    {
        subject.Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());

        return subject;
    }

    private static List<string> TrackPropertyChanged(INotifyPropertyChanged subject)
    {
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);
        return firedEvents;
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests`
Expected: build fails, `SonosSystem`, `SonosPlayer`, `SonosGroup` do not exist.

- [ ] **Step 5: Implement `SonosDevice.cs`**

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A physical Sonos unit: a room player or a satellite bonded to one.
/// </summary>
[InterceptorSubject]
public partial class SonosDevice :
    IDeviceInfo,
    INetworkAdapter,
    ISoftwareState,
    IConnectionState,
    ITitleProvider,
    IIconProvider
{
    private bool _hasStaticData;
    private string? _staticDataFirmwareBuild;

    internal SonosDevice(string uuid)
    {
        Uuid = uuid;
        RoomName = string.Empty;
        ServiceIds = new HashSet<string>(StringComparer.Ordinal);
        IsReachable = true;
    }

    /// <summary>
    /// The RINCON identifier of the unit, stable across restarts and IP changes.
    /// </summary>
    [State(Position = 1)]
    public partial string Uuid { get; internal set; }

    [State(Position = 2)]
    public partial string RoomName { get; internal set; }

    internal partial Uri? BaseUri { get; set; }

    /// <summary>
    /// The firmware build from the topology; a change means the zone info has to be read again.
    /// </summary>
    internal partial string? FirmwareBuild { get; set; }

    internal partial IReadOnlySet<string> ServiceIds { get; set; }

    internal partial bool IsInTopology { get; set; }

    internal partial bool IsReachable { get; set; }

    [Derived]
    public string? Manufacturer => "Sonos";

    public partial string? Model { get; internal set; }

    public partial string? ProductCode { get; internal set; }

    public partial string? SerialNumber { get; internal set; }

    public partial string? HardwareRevision { get; internal set; }

    [Derived]
    public string? IpAddress => BaseUri?.Host;

    public partial string? MacAddress { get; internal set; }

    [Derived]
    public string? SubnetMask => null;

    [Derived]
    public string? Gateway => null;

    public partial bool? IsWireless { get; internal set; }

    [Derived]
    public int? SignalStrength => null;

    public partial string? SoftwareVersion { get; internal set; }

    [Derived]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public bool IsConnected => IsInTopology && IsReachable;

    [State(Position = 901)]
    public partial string? StatusMessage { get; internal set; }

    [Derived]
    public string? Title => $"{Model ?? "Sonos"} ({RoomName})";

    [Derived]
    public virtual string? IconName => "Speaker";

    [Derived]
    public string? IconColor => IsConnected ? null : "Error";

    internal bool NeedsStaticData => !_hasStaticData || _staticDataFirmwareBuild != FirmwareBuild;

    internal void ApplyTopology(string roomName, Uri baseUri, string? firmwareBuild, bool? isWireless)
    {
        RoomName = roomName;
        BaseUri = baseUri;
        FirmwareBuild = firmwareBuild;
        IsWireless = isWireless;
        IsInTopology = true;
    }

    internal void ApplyStaticData(
        SonosDeviceDescription description,
        string? serialNumber,
        string? macAddress,
        string? hardwareRevision,
        string? softwareVersion)
    {
        Model = description.ModelName;
        ProductCode = description.ModelNumber;
        ServiceIds = description.ServiceIds;
        SerialNumber = SonosValues.NullIfEmpty(serialNumber);
        MacAddress = SonosValues.NullIfEmpty(macAddress);
        HardwareRevision = SonosValues.NullIfEmpty(hardwareRevision);
        SoftwareVersion = SonosValues.NullIfEmpty(softwareVersion);
        _hasStaticData = true;
        _staticDataFirmwareBuild = FirmwareBuild;
    }

    internal void InvalidateStaticData() => _hasStaticData = false;

    internal void MarkMissing() => IsInTopology = false;

    internal void ReportPollSucceeded()
    {
        IsReachable = true;
        StatusMessage = null;
    }

    /// <summary>
    /// Records a failed poll and returns whether the failure is new, so the caller logs it once.
    /// </summary>
    internal bool ReportPollFailed(string message)
    {
        var isNew = IsReachable || StatusMessage != message;
        IsReachable = false;
        StatusMessage = message;
        return isNew;
    }
}
```

- [ ] **Step 6: Implement `SonosSatellite.cs`**

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A unit bonded to a room player: a surround, a subwoofer or the second speaker of a stereo pair.
/// </summary>
[InterceptorSubject]
public partial class SonosSatellite : SonosDevice
{
    internal SonosSatellite(string uuid)
        : base(uuid)
    {
        Role = SonosSatelliteRole.Other;
    }

    [State(Position = 3)]
    public partial SonosSatelliteRole Role { get; internal set; }

    [Derived]
    public override string? IconName => Role == SonosSatelliteRole.Subwoofer ? "SurroundSound" : "Speaker";
}
```

- [ ] **Step 7: Implement `SonosPlayer.cs`**

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A visible Sonos room player.
/// </summary>
[InterceptorSubject]
public partial class SonosPlayer : SonosDevice,
    IAudioPlayerState,
    IMediaTrackState,
    IBatteryState
{
    private readonly SonosSystem _system;

    // Guards the event versus poll ordering: a timestamp and the fields it protects change together.
    private readonly Lock _stateLock = new();
    private DateTimeOffset _lastAvTransportEventAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRenderingControlEventAt = DateTimeOffset.MinValue;

    internal SonosPlayer(SonosSystem system, string uuid)
        : base(uuid)
    {
        _system = system;
        TransportState = SonosTransportState.Unknown;
        Satellites = new Dictionary<string, SonosSatellite>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The media the player was told to play (AVTransportURI), which identifies the source better than the track.
    /// </summary>
    internal partial string? MediaUri { get; set; }

    [State(Position = 10)]
    public partial SonosTransportState TransportState { get; internal set; }

    [Derived]
    public bool? IsPlaying => TransportState switch
    {
        SonosTransportState.Playing or SonosTransportState.Transitioning => true,
        SonosTransportState.Unknown => null,
        _ => false
    };

    public partial bool? IsMuted { get; internal set; }

    public partial decimal? Volume { get; internal set; }

    public partial string? CurrentTrackTitle { get; internal set; }

    public partial string? CurrentTrackArtist { get; internal set; }

    public partial string? CurrentTrackAlbum { get; internal set; }

    public partial string? CurrentTrackImageUri { get; internal set; }

    public partial string? CurrentTrackUri { get; internal set; }

    public partial TimeSpan? CurrentTrackPosition { get; internal set; }

    public partial TimeSpan? CurrentTrackDuration { get; internal set; }

    [Derived]
    [State(Position = 11)]
    public SonosSource Source => SonosValues.DetectSource(MediaUri ?? CurrentTrackUri);

    [State(Position = 12)]
    public partial bool? Shuffle { get; internal set; }

    [State(Position = 13)]
    public partial SonosRepeatMode? Repeat { get; internal set; }

    [State(Position = 14)]
    public partial TimeSpan? SleepTimerRemaining { get; internal set; }

    [State(Position = 20)]
    public partial int? Bass { get; internal set; }

    [State(Position = 21)]
    public partial int? Treble { get; internal set; }

    [State(Position = 22)]
    public partial bool? Loudness { get; internal set; }

    [State(Position = 23)]
    public partial bool? NightMode { get; internal set; }

    [State(Position = 24)]
    public partial bool? SpeechEnhancement { get; internal set; }

    [State(Position = 30)]
    public partial string? GroupCoordinatorUuid { get; internal set; }

    [Derived]
    [State(Position = 31)]
    public bool IsGroupCoordinator => GroupCoordinatorUuid is null || GroupCoordinatorUuid == Uuid;

    [Derived]
    [State(Position = 32)]
    public bool IsHomeTheater => ServiceIds.Contains("HTControl");

    [Derived]
    [State(Position = 33)]
    public bool HasLineIn => ServiceIds.Contains("AudioIn");

    public partial decimal? BatteryLevel { get; internal set; }

    [State(Position = 341)]
    public partial bool? IsCharging { get; internal set; }

    [State(Position = 40)]
    public partial Dictionary<string, SonosSatellite> Satellites { get; internal set; }

    [Derived]
    public override string? IconName => IsPlaying == true ? "PlayCircle" : "Speaker";

    // Operation availability. The attribute names match the operation method names without "Async".

    private bool CanControl => IsConnected && _system.IsConnected;

    [Derived]
    [PropertyAttribute("Play", KnownAttributes.IsEnabled)]
    public bool Play_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Pause", KnownAttributes.IsEnabled)]
    public bool Pause_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Next", KnownAttributes.IsEnabled)]
    public bool Next_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Previous", KnownAttributes.IsEnabled)]
    public bool Previous_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("TogglePlayback", KnownAttributes.IsEnabled)]
    public bool TogglePlayback_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Seek", KnownAttributes.IsEnabled)]
    public bool Seek_IsEnabled => CanControl && CurrentTrackDuration > TimeSpan.Zero;

    [Derived]
    [PropertyAttribute("SwitchToTv", KnownAttributes.IsEnabled)]
    public bool SwitchToTv_IsEnabled => CanControl && IsHomeTheater;

    [Derived]
    [PropertyAttribute("SwitchToLineIn", KnownAttributes.IsEnabled)]
    public bool SwitchToLineIn_IsEnabled => CanControl && HasLineIn;

    [Derived]
    [PropertyAttribute("SetNightMode", KnownAttributes.IsEnabled)]
    public bool SetNightMode_IsEnabled => CanControl && IsHomeTheater;

    [Derived]
    [PropertyAttribute("SetSpeechEnhancement", KnownAttributes.IsEnabled)]
    public bool SetSpeechEnhancement_IsEnabled => CanControl && IsHomeTheater;

    [Derived]
    [PropertyAttribute("LeaveGroup", KnownAttributes.IsEnabled)]
    public bool LeaveGroup_IsEnabled => CanControl && !IsGroupCoordinator;

    internal void ApplyPlayerTopology(SonosTopologyPlayer topology, string coordinatorUuid)
    {
        ApplyTopology(topology.RoomName, topology.BaseUri, topology.SoftwareVersion, topology.IsWireless);
        GroupCoordinatorUuid = coordinatorUuid;

        var (batteryLevel, isCharging) = SonosValues.ParseBattery(topology.MoreInfo);
        BatteryLevel = batteryLevel;
        IsCharging = isCharging;

        var satellites = Satellites;
        Dictionary<string, SonosSatellite>? updatedSatellites = null;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var satelliteTopology in topology.Satellites)
        {
            present.Add(satelliteTopology.Uuid);
            if (!satellites.TryGetValue(satelliteTopology.Uuid, out var satellite))
            {
                satellite = new SonosSatellite(satelliteTopology.Uuid);
                updatedSatellites ??= new Dictionary<string, SonosSatellite>(satellites, StringComparer.Ordinal);
                updatedSatellites[satelliteTopology.Uuid] = satellite;
            }

            satellite.Role = satelliteTopology.Role;
            satellite.ApplyTopology(satelliteTopology.RoomName, satelliteTopology.BaseUri, satelliteTopology.SoftwareVersion, satelliteTopology.IsWireless);
        }

        foreach (var (uuid, satellite) in satellites)
        {
            if (!present.Contains(uuid))
            {
                satellite.MarkMissing();
            }
        }

        if (updatedSatellites is not null)
        {
            Satellites = updatedSatellites;
        }
    }

    internal void ApplyAvTransportEvent(AvTransportChange change, DateTimeOffset receivedAt)
    {
        lock (_stateLock)
        {
            _lastAvTransportEventAt = receivedAt;
            ApplyAvTransport(change);
        }
    }

    internal void ApplyRenderingControlEvent(RenderingControlChange change, DateTimeOffset receivedAt)
    {
        lock (_stateLock)
        {
            _lastRenderingControlEventAt = receivedAt;
            ApplyRenderingControl(change);
        }
    }

    internal void ApplyPoll(SonosPlayerReading reading, DateTimeOffset pollStartedAt)
    {
        lock (_stateLock)
        {
            // A poll that started before the latest event read state the event has since replaced.
            if (_lastAvTransportEventAt <= pollStartedAt)
            {
                ApplyAvTransport(reading.AvTransport);
            }

            if (_lastRenderingControlEventAt <= pollStartedAt)
            {
                ApplyRenderingControl(reading.RenderingControl);
            }

            CurrentTrackPosition = reading.Position;
            SleepTimerRemaining = reading.SleepTimerRemaining;
        }
    }

    private void ApplyAvTransport(AvTransportChange change)
    {
        if (change.TransportState is not null)
        {
            TransportState = SonosValues.ParseTransportState(change.TransportState);
        }

        if (SonosValues.ParsePlayMode(change.PlayMode) is { } playMode)
        {
            Shuffle = playMode.Shuffle;
            Repeat = playMode.Repeat;
        }

        if (SonosValues.IsKnown(change.MediaUri))
        {
            MediaUri = SonosValues.NullIfEmpty(change.MediaUri);
        }

        if (SonosValues.IsKnown(change.TrackUri))
        {
            CurrentTrackUri = SonosValues.NullIfEmpty(change.TrackUri);
        }

        if (SonosValues.IsKnown(change.TrackDuration))
        {
            CurrentTrackDuration = SonosValues.ParseDuration(change.TrackDuration);
        }

        if (SonosValues.IsKnown(change.TrackMetaData))
        {
            var track = DidlParser.ParseTrack(change.TrackMetaData);
            CurrentTrackTitle = track?.Title;
            CurrentTrackArtist = track?.Artist;
            CurrentTrackAlbum = track?.Album;
            CurrentTrackImageUri = SonosValues.ToAbsoluteUri(track?.AlbumArtUri, BaseUri);
        }
    }

    private void ApplyRenderingControl(RenderingControlChange change)
    {
        if (change.Volume is { } volume)
        {
            Volume = SonosValues.ToVolume(volume);
        }

        if (change.Mute is { } mute)
        {
            IsMuted = mute;
        }

        if (change.Bass is { } bass)
        {
            Bass = bass;
        }

        if (change.Treble is { } treble)
        {
            Treble = treble;
        }

        if (change.Loudness is { } loudness)
        {
            Loudness = loudness;
        }

        if (change.NightMode is { } nightMode)
        {
            NightMode = nightMode;
        }

        if (change.SpeechEnhancement is { } speechEnhancement)
        {
            SpeechEnhancement = speechEnhancement;
        }
    }
}
```

The `*_IsEnabled` properties live here rather than in Task 10 so `_system` is read from the start (Sonar S4487 rejects unread private fields). Their operations arrive in Task 10; until then the attributes simply name operations that do not exist yet.

- [ ] **Step 8: Implement `SonosGroup.cs`**

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Rooms playing together. Playback and track state are the coordinator's; volume and mute are the group's.
/// </summary>
[InterceptorSubject]
public partial class SonosGroup :
    IAudioPlayerState,
    IMediaTrackState,
    IVirtualSubject,
    ITitleProvider,
    IIconProvider
{
    private readonly SonosSystem _system;
    private readonly Lock _stateLock = new();
    private DateTimeOffset _lastEventAt = DateTimeOffset.MinValue;

    internal SonosGroup(SonosSystem system, SonosPlayer coordinator)
    {
        _system = system;
        GroupId = string.Empty;
        Coordinator = coordinator;
        Members = [coordinator];
    }

    /// <summary>
    /// The current Sonos group id. It changes on every regroup, so the group is keyed by its coordinator instead.
    /// </summary>
    [State(Position = 1)]
    public partial string GroupId { get; internal set; }

    [State(Position = 2)]
    public partial SonosPlayer Coordinator { get; internal set; }

    [State(Position = 3)]
    public partial SonosPlayer[] Members { get; internal set; }

    public partial decimal? Volume { get; internal set; }

    public partial bool? IsMuted { get; internal set; }

    [Derived]
    public bool? IsPlaying => Coordinator.IsPlaying;

    [Derived]
    public string? CurrentTrackTitle => Coordinator.CurrentTrackTitle;

    [Derived]
    public string? CurrentTrackArtist => Coordinator.CurrentTrackArtist;

    [Derived]
    public string? CurrentTrackAlbum => Coordinator.CurrentTrackAlbum;

    [Derived]
    public string? CurrentTrackImageUri => Coordinator.CurrentTrackImageUri;

    [Derived]
    public string? CurrentTrackUri => Coordinator.CurrentTrackUri;

    [Derived]
    public TimeSpan? CurrentTrackPosition => Coordinator.CurrentTrackPosition;

    [Derived]
    public TimeSpan? CurrentTrackDuration => Coordinator.CurrentTrackDuration;

    [Derived]
    public string? Title => string.Join(" + ", Members.Select(member => member.RoomName));

    [Derived]
    public string? IconName => "SpeakerGroup";

    private bool CanControl => Coordinator.IsConnected && _system.IsConnected;

    [Derived]
    [PropertyAttribute("Play", KnownAttributes.IsEnabled)]
    public bool Play_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Pause", KnownAttributes.IsEnabled)]
    public bool Pause_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Seek", KnownAttributes.IsEnabled)]
    public bool Seek_IsEnabled => CanControl && CurrentTrackDuration > TimeSpan.Zero;

    internal void Update(string groupId, SonosPlayer[] members)
    {
        GroupId = groupId;
        if (!members.SequenceEqual(Members))
        {
            Members = members;
        }
    }

    internal void ApplyGroupRenderingControlEvent(GroupRenderingControlChange change, DateTimeOffset receivedAt)
    {
        lock (_stateLock)
        {
            _lastEventAt = receivedAt;
            Apply(change);
        }
    }

    internal void ApplyGroupRenderingControlPoll(GroupRenderingControlChange change, DateTimeOffset pollStartedAt)
    {
        lock (_stateLock)
        {
            if (_lastEventAt <= pollStartedAt)
            {
                Apply(change);
            }
        }
    }

    private void Apply(GroupRenderingControlChange change)
    {
        if (change.Volume is { } volume)
        {
            Volume = SonosValues.ToVolume(volume);
        }

        if (change.Mute is { } mute)
        {
            IsMuted = mute;
        }
    }
}
```

As in `SonosPlayer`, the `*_IsEnabled` properties are added here so `_system` is read from the start.

- [ ] **Step 9: Implement `SonosSystem.cs`**

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos household: its room players, their bonded satellites and the groups they form.
/// </summary>
[Category("Devices")]
[Description("Sonos household with its room players, satellites and groups")]
[InterceptorSubject]
public partial class SonosSystem : BackgroundService,
    IConfigurable,
    IHubDevice,
    IMonitoredService,
    IConnectionState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal const int DefaultEventPort = 6329;
    internal static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<SonosSystem> _logger;

    // Topology arrives from the poll and from ZoneGroupTopology events, so applying it is serialized.
    private readonly Lock _topologyLock = new();
    private volatile IReadOnlyList<SonosFavorite> _favorites = [];

    /// <summary>
    /// Any speaker of the household as host or host:port. Empty uses the last known speakers, then SSDP.
    /// </summary>
    [Configuration]
    public partial string? SeedHost { get; set; }

    /// <summary>
    /// The address speakers send events to. Empty detects the local address that routes to the seed speaker.
    /// </summary>
    [Configuration]
    public partial string? EventCallbackHost { get; set; }

    [Configuration]
    public partial int EventPort { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State(Position = 1)]
    public partial Dictionary<string, SonosPlayer> Players { get; internal set; }

    [State(Position = 2)]
    public partial Dictionary<string, SonosGroup> Groups { get; internal set; }

    /// <summary>
    /// The names of the favorites <see cref="SonosPlayer.PlayFavoriteAsync"/> accepts.
    /// </summary>
    [State(Position = 3)]
    public partial string[] Favorites { get; internal set; }

    [State(Position = 4)]
    public partial bool AreEventsActive { get; internal set; }

    [State(Position = 5)]
    public partial string? ActiveEventCallbackHost { get; internal set; }

    public partial bool IsConnected { get; internal set; }

    public partial ServiceStatus Status { get; internal set; }

    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => "Sonos System";

    [Derived]
    public string? IconName => "LibraryMusic";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : Status == ServiceStatus.Error ? "Error" : null;

    // Read by the connection loop in SonosSystem.Runtime.cs.
    internal IHttpClientFactory HttpClientFactory { get; }

    public SonosSystem(IHttpClientFactory httpClientFactory, ILogger<SonosSystem> logger)
    {
        HttpClientFactory = httpClientFactory;
        _logger = logger;

        EventPort = DefaultEventPort;
        PollingInterval = DefaultPollingInterval;
        RetryInterval = DefaultRetryInterval;
        Players = new Dictionary<string, SonosPlayer>(StringComparer.Ordinal);
        Groups = new Dictionary<string, SonosGroup>(StringComparer.Ordinal);
        Favorites = [];
        Status = ServiceStatus.Stopped;
    }

    internal void ApplyTopology(SonosTopology topology)
    {
        lock (_topologyLock)
        {
            var players = Players;
            Dictionary<string, SonosPlayer>? updatedPlayers = null;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in topology.Groups)
            {
                foreach (var topologyPlayer in group.Players)
                {
                    present.Add(topologyPlayer.Uuid);
                    if (!players.TryGetValue(topologyPlayer.Uuid, out var player))
                    {
                        player = new SonosPlayer(this, topologyPlayer.Uuid);
                        updatedPlayers ??= new Dictionary<string, SonosPlayer>(players, StringComparer.Ordinal);
                        updatedPlayers[topologyPlayer.Uuid] = player;
                        _logger.LogInformation("Found the Sonos player {Room} ({Uuid}).", topologyPlayer.RoomName, topologyPlayer.Uuid);
                    }

                    player.ApplyPlayerTopology(topologyPlayer, group.CoordinatorUuid);
                }
            }

            foreach (var (uuid, player) in players)
            {
                if (!present.Contains(uuid))
                {
                    player.MarkMissing();
                    foreach (var satellite in player.Satellites.Values)
                    {
                        satellite.MarkMissing();
                    }
                }
            }

            if (updatedPlayers is not null)
            {
                Players = updatedPlayers;
            }

            ApplyGroups(topology, Players);
        }
    }

    // Caller holds _topologyLock.
    private void ApplyGroups(SonosTopology topology, Dictionary<string, SonosPlayer> players)
    {
        var groups = Groups;
        var updatedGroups = new Dictionary<string, SonosGroup>(StringComparer.Ordinal);
        foreach (var topologyGroup in topology.Groups)
        {
            if (!players.TryGetValue(topologyGroup.CoordinatorUuid, out var coordinator))
            {
                continue;
            }

            if (!groups.TryGetValue(topologyGroup.CoordinatorUuid, out var group))
            {
                group = new SonosGroup(this, coordinator);
            }

            group.Update(topologyGroup.Id, topologyGroup.Players.Select(member => players[member.Uuid]).ToArray());
            updatedGroups[topologyGroup.CoordinatorUuid] = group;
        }

        if (!HaveSameEntries(groups, updatedGroups))
        {
            Groups = updatedGroups;
        }
    }

    internal SonosPlayer? FindPlayer(string roomNameOrUuid)
    {
        var players = Players;
        if (players.TryGetValue(roomNameOrUuid, out var byUuid))
        {
            return byUuid;
        }

        return players.Values.FirstOrDefault(player =>
            string.Equals(player.RoomName, roomNameOrUuid, StringComparison.OrdinalIgnoreCase));
    }

    internal SonosFavorite? FindFavorite(string name) =>
        _favorites.FirstOrDefault(favorite => string.Equals(favorite.Title, name, StringComparison.OrdinalIgnoreCase));

    internal void SetFavorites(IReadOnlyList<SonosFavorite> favorites)
    {
        _favorites = favorites;
        var names = favorites.Select(favorite => favorite.Title).ToArray();
        if (!names.SequenceEqual(Favorites))
        {
            Favorites = names;
        }
    }

    internal ArgumentException CreateUnknownRoomException(string value, string parameterName) =>
        new($"Unknown Sonos room '{value}'. Known rooms: {string.Join(", ", Players.Values.Select(player => player.RoomName))}.", parameterName);

    private static bool HaveSameEntries<T>(Dictionary<string, T> existing, Dictionary<string, T> updated)
        where T : class
    {
        if (existing.Count != updated.Count)
        {
            return false;
        }

        foreach (var (key, value) in existing)
        {
            if (!updated.TryGetValue(key, out var updatedValue) || !ReferenceEquals(value, updatedValue))
            {
                return false;
            }
        }

        return true;
    }
}
```

- [ ] **Step 10: Add the temporary runtime file** (Task 9 replaces this file completely)

`src/HomeBlaze/Namotion.Devices.Sonos/SonosSystem.Runtime.cs`:

```csharp
namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Delay(Timeout.Infinite, stoppingToken);
}
```

- [ ] **Step 11: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests`
Expected: all tests pass. If the generator reports a diagnostic for `SonosSystem` being split across two files, move the two runtime members into `SonosSystem.cs` for now and keep doing so in Task 9.

- [ ] **Step 12: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: model the Sonos household as players, satellites and groups

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Sonos.Base connection, discovery and fake speaker

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/Client/SonosClientProvider.cs`, `Client/SonosConnection.cs`, `Client/SonosDiscovery.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/FakeSonosSpeaker.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosConnectionTests.cs`, `SonosDiscoveryTests.cs`

**Interfaces:**
- Consumes: parsers (Tasks 3 to 5), `SonosValues` (Task 2), `LoopbackHttpServer`, `TestFixtures` (tests).
- Produces (namespace `Namotion.Devices.Sonos.Client`):
  - `sealed class SonosClientProvider(HttpClient httpClient) : ISonosServiceProvider`.
  - `sealed class SonosConnection : IDisposable` with `SonosConnection(Uri baseUri, string? uuid, HttpClient httpClient, ISonosServiceProvider provider)`, `Uri BaseUri`; reads `ReadDescriptionAsync`, `ReadZoneInfoAsync` (returns `SonosZoneInfo(string? SerialNumber, string? MacAddress, string? HardwareVersion, string? DisplayVersion)`), `ReadTopologyAsync`, `ReadFavoritesAsync`, `ReadPlayerAsync(bool isHomeTheater, CancellationToken)`, `ReadGroupAsync`; commands `PlayAsync`, `PauseAsync`, `StopAsync`, `NextAsync`, `PreviousAsync`, `TogglePlaybackAsync`, `SeekAsync(TimeSpan)`, `SetVolumeAsync(int)`, `ChangeVolumeAsync(int)`, `RampVolumeAsync(int)`, `SetMuteAsync(bool)`, `SetTransportUriAsync(string uri, string metadata)`, `PlayFromQueueAsync(string uri, string metadata)`, `PlayNotificationAsync(Uri soundUri, int volume)`, `SwitchToTvAsync`, `SwitchToLineInAsync`, `SetPlayModeAsync(string)`, `SetSleepTimerAsync(TimeSpan)`, `SetBassAsync(int)`, `SetTrebleAsync(int)`, `SetLoudnessAsync(bool)`, `SetEqualizerAsync(string type, bool enabled)`, `JoinAsync(string coordinatorUuid)`, `LeaveGroupAsync`, `SetGroupVolumeAsync(int)`, `ChangeGroupVolumeAsync(int)`, `SetGroupMuteAsync(bool)`; every command takes a trailing `CancellationToken`.
  - `static class SonosDiscovery` with `Uri CreateDeviceUri(string host)`, `string? DetectLocalAddress(string remoteHost)`, `Task<Uri?> FindSpeakerAsync(CancellationToken)`.
- Produces (tests): `FakeSonosSpeaker` with `Uri BaseUri`, `string Host`, `string DeviceDescription`, `IReadOnlyCollection<SoapCall> Calls`, `IReadOnlyCollection<string> Unsubscribed`, `void Respond(string action, params (string Name, string Value)[] values)`, `void RespondAsIdlePlayer(string uuid, string roomName)`, `string? GetCallback(string eventPath)`, `static string SidFor(string eventPath)`; `record SoapCall(string Service, string Action, string Body)`.

- [ ] **Step 1: Add the fake speaker**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/FakeSonosSpeaker.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A loopback Sonos speaker: answers SOAP actions with canned values, serves a device description and
/// accepts event subscriptions, recording every request.
/// </summary>
internal sealed class FakeSonosSpeaker : IAsyncDisposable
{
    private readonly LoopbackHttpServer _server;
    private readonly ConcurrentDictionary<string, string> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SoapCall> _calls = new();
    private readonly ConcurrentDictionary<string, string> _callbacks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _unsubscribed = new();

    internal FakeSonosSpeaker()
    {
        DeviceDescription = TestFixtures.Read("device-description-ray.xml");
        _server = new LoopbackHttpServer(HandleAsync);
    }

    internal Uri BaseUri => _server.BaseUri;

    internal string Host => $"127.0.0.1:{_server.Port}";

    internal string DeviceDescription { get; set; }

    internal IReadOnlyCollection<SoapCall> Calls => _calls.ToArray();

    internal IReadOnlyCollection<string> Unsubscribed => _unsubscribed.ToArray();

    internal static string SidFor(string eventPath) => "uuid:" + eventPath.Trim('/').Replace('/', '-');

    internal string? GetCallback(string eventPath) =>
        _callbacks.TryGetValue(eventPath, out var callback) ? callback : null;

    internal void Respond(string action, params (string Name, string Value)[] values) =>
        _responses[action] = string.Concat(values.Select(value => $"<{value.Name}>{SecurityElement.Escape(value.Value)}</{value.Name}>"));

    /// <summary>
    /// Answers as a single-room household whose only player is this speaker, paused on Spotify Connect.
    /// </summary>
    internal void RespondAsIdlePlayer(string uuid, string roomName)
    {
        const string spotifyUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf";
        Respond("GetZoneGroupState", ("ZoneGroupState",
            $"""<ZoneGroupState><ZoneGroups><ZoneGroup Coordinator="{uuid}" ID="{uuid}:1"><ZoneGroupMember UUID="{uuid}" Location="{BaseUri}xml/device_description.xml" ZoneName="{roomName}" SoftwareVersion="97.1-80312" EthLink="0" MoreInfo="" /></ZoneGroup></ZoneGroups></ZoneGroupState>"""));
        Respond("GetZoneInfo",
            ("SerialNumber", "00-00-00-00-00-06:D"), ("SoftwareVersion", "97.1-80312"), ("DisplaySoftwareVersion", "18.8"),
            ("HardwareVersion", "1.38.1.10-2.1"), ("IPAddress", "127.0.0.1"), ("MACAddress", "00:00:00:00:00:06"),
            ("CopyrightInfo", "c"), ("ExtraInfo", ""), ("HTAudioIn", "0"), ("Flags", "0"));
        Respond("GetTransportInfo", ("CurrentTransportState", "PAUSED_PLAYBACK"), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
        Respond("GetTransportSettings", ("PlayMode", "NORMAL"), ("RecQualityMode", "NOT_IMPLEMENTED"));
        Respond("GetMediaInfo", ("NrTracks", "1"), ("MediaDuration", "NOT_IMPLEMENTED"), ("CurrentURI", spotifyUri), ("CurrentURIMetaData", ""));
        Respond("GetPositionInfo",
            ("Track", "1"), ("TrackDuration", "NOT_IMPLEMENTED"), ("TrackMetaData", "NOT_IMPLEMENTED"), ("TrackURI", spotifyUri),
            ("RelTime", "NOT_IMPLEMENTED"), ("AbsTime", "NOT_IMPLEMENTED"), ("RelCount", "2147483647"), ("AbsCount", "2147483647"));
        Respond("GetRemainingSleepTimerDuration", ("RemainingSleepTimerDuration", ""), ("CurrentSleepTimerGeneration", "0"));
        Respond("GetVolume", ("CurrentVolume", "44"));
        Respond("GetMute", ("CurrentMute", "0"));
        Respond("GetBass", ("CurrentBass", "0"));
        Respond("GetTreble", ("CurrentTreble", "0"));
        Respond("GetLoudness", ("CurrentLoudness", "1"));
        Respond("GetEQ", ("CurrentValue", "1"));
        Respond("GetGroupVolume", ("CurrentVolume", "44"));
        Respond("GetGroupMute", ("CurrentMute", "0"));
        Respond("Browse", ("Result", TestFixtures.Read("favorites.xml")), ("NumberReturned", "4"), ("TotalMatches", "4"), ("UpdateID", "1"));
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;

        switch (request.HttpMethod)
        {
            case "GET" when path == "/xml/device_description.xml":
                await WriteAsync(response, DeviceDescription);
                return;

            case "SUBSCRIBE":
                if (request.Headers["CALLBACK"] is { } callback)
                {
                    _callbacks[path] = callback.Trim('<', '>');
                }

                response.Headers["SID"] = SidFor(path);
                response.Headers["TIMEOUT"] = "Second-1800";
                return;

            case "UNSUBSCRIBE":
                _unsubscribed.Enqueue(path);
                return;

            case "POST":
                // "urn:schemas-upnp-org:service:AVTransport:1#Play"
                var soapAction = (request.Headers["SOAPACTION"] ?? string.Empty).Trim('"');
                var action = soapAction[(soapAction.LastIndexOf('#') + 1)..];
                var service = soapAction.Split(':') is { Length: >= 4 } parts ? parts[3] : string.Empty;

                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                _calls.Enqueue(new SoapCall(service, action, body));
                var values = _responses.GetValueOrDefault(action, string.Empty);
                await WriteAsync(response,
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    $"<s:Body><u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:{service}:1\">{values}</u:{action}Response></s:Body></s:Envelope>");
                return;

            default:
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        response.ContentType = "text/xml; charset=\"utf-8\"";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

internal sealed record SoapCall(string Service, string Action, string Body);
```

- [ ] **Step 2: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosConnectionTests.cs`:

```csharp
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosConnectionTests
{
    private const string Uuid = TestFixtures.KitchenUuid;

    private static SonosConnection CreateConnection(FakeSonosSpeaker speaker, HttpClient httpClient) =>
        new(speaker.BaseUri, Uuid, httpClient, new SonosClientProvider(httpClient));

    [Fact]
    public async Task WhenReadingPlayer_ThenSoapResponsesAreMapped()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: true, CancellationToken.None);

        // Assert
        Assert.Equal("PAUSED_PLAYBACK", reading.AvTransport.TransportState);
        Assert.Equal("NORMAL", reading.AvTransport.PlayMode);
        Assert.StartsWith("x-sonos-vli:", reading.AvTransport.MediaUri);
        Assert.Equal("NOT_IMPLEMENTED", reading.AvTransport.TrackMetaData);
        Assert.Null(reading.Position);
        Assert.Null(reading.SleepTimerRemaining);
        Assert.Equal(44, reading.RenderingControl.Volume);
        Assert.False(reading.RenderingControl.Mute);
        Assert.True(reading.RenderingControl.Loudness);
        Assert.True(reading.RenderingControl.NightMode);
        Assert.Contains(speaker.Calls, call => call.Action == "GetEQ" && call.Body.Contains("<EQType>DialogLevel</EQType>"));
    }

    [Fact]
    public async Task WhenReadingPlayerWithoutHomeTheater_ThenEqualizerIsNotQueried()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: false, CancellationToken.None);

        // Assert
        Assert.Null(reading.RenderingControl.NightMode);
        Assert.DoesNotContain(speaker.Calls, call => call.Action == "GetEQ");
    }

    [Fact]
    public async Task WhenReadingTopologyFavoritesAndDescription_ThenTheyAreParsed()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var topology = await connection.ReadTopologyAsync(CancellationToken.None);
        var favorites = await connection.ReadFavoritesAsync(CancellationToken.None);
        var description = await connection.ReadDescriptionAsync(CancellationToken.None);
        var zoneInfo = await connection.ReadZoneInfoAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Küche", Assert.Single(Assert.Single(topology.Groups).Players).RoomName);
        Assert.Equal(2, favorites.Count);
        Assert.Equal("Sonos Ray", description.ModelName);
        Assert.Equal("18.8", zoneInfo.DisplayVersion);
        Assert.Equal("00:00:00:00:00:06", zoneInfo.MacAddress);
    }

    [Fact]
    public async Task WhenSettingVolume_ThenSendsDesiredVolumeOnMasterChannel()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetVolumeAsync(50, CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("RenderingControl", call.Service);
        Assert.Equal("SetVolume", call.Action);
        Assert.Contains("<DesiredVolume>50</DesiredVolume>", call.Body);
        Assert.Contains("<Channel>Master</Channel>", call.Body);
    }

    [Fact]
    public async Task WhenSetVolumeIsRejected_ThenThrows()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = new SonosConnection(
            new Uri($"http://127.0.0.1:{LoopbackHttpServer.GetFreePort()}/"), Uuid, httpClient, new SonosClientProvider(httpClient));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.SetVolumeAsync(50, CancellationToken.None));
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosDiscoveryTests.cs`:

```csharp
using Namotion.Devices.Sonos.Client;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosDiscoveryTests
{
    [Theory]
    [InlineData("10.0.0.5", "http://10.0.0.5:1400/")]
    [InlineData(" 10.0.0.5 ", "http://10.0.0.5:1400/")]
    [InlineData("127.0.0.1:5000", "http://127.0.0.1:5000/")]
    [InlineData("sonos-kitchen.local", "http://sonos-kitchen.local:1400/")]
    public void WhenCreatingDeviceUri_ThenDefaultsToSonosPort(string host, string expected)
    {
        // Act
        var uri = SonosDiscovery.CreateDeviceUri(host);

        // Assert
        Assert.Equal(new Uri(expected), uri);
    }

    [Fact]
    public void WhenDetectingLocalAddressForLoopback_ThenReturnsLoopback()
    {
        // Act
        var address = SonosDiscovery.DetectLocalAddress("127.0.0.1");

        // Assert
        Assert.Equal("127.0.0.1", address);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~SonosConnectionTests|FullyQualifiedName~SonosDiscoveryTests"`
Expected: build fails, `SonosConnection`, `SonosDiscovery`, `SonosClientProvider` do not exist.

- [ ] **Step 4: Implement `SonosClientProvider.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Sonos.Base;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// Hands Sonos.Base the one HttpClient of the current connection, so all service calls share its handler and
/// timeout. Sonos.Base never disposes the client it is given. Events go through SonosEventListener instead of
/// a Sonos.Base event bus.
/// </summary>
internal sealed class SonosClientProvider(HttpClient httpClient) : ISonosServiceProvider
{
    public HttpClient GetHttpClient() => httpClient;

    public IHttpClientFactory? GetHttpClientFactory() => null;

    public ILogger<TCategoryName>? CreateLogger<TCategoryName>() => null;

    public ILogger? CreateLogger(string categoryName) => null;

    public ILoggerFactory? GetLoggerFactory() => null;

    public ISonosEventBus? GetSonosEventBus() => null;
}
```

- [ ] **Step 5: Implement `SonosConnection.cs`**

```csharp
using Namotion.Devices.Sonos.Parsing;
using Sonos.Base;
using Sonos.Base.Services;

// Our own SonosDevice subject lives in an enclosing namespace and would win over the using directive.
using SonosBaseDevice = Sonos.Base.SonosDevice;

namespace Namotion.Devices.Sonos.Client;

internal sealed record SonosZoneInfo(string? SerialNumber, string? MacAddress, string? HardwareVersion, string? DisplayVersion);

/// <summary>
/// The SOAP connection to one Sonos unit. Reads return our records; commands throw on a SOAP fault.
/// </summary>
internal sealed class SonosConnection : IDisposable
{
    private const int InstanceId = 0;
    private const string MasterChannel = "Master";

    private readonly HttpClient _httpClient;
    private readonly SonosBaseDevice _device;

    internal SonosConnection(Uri baseUri, string? uuid, HttpClient httpClient, ISonosServiceProvider provider)
    {
        BaseUri = baseUri;
        _httpClient = httpClient;
        _device = new SonosBaseDevice(new SonosDeviceOptions(baseUri, provider, uuid));
    }

    internal Uri BaseUri { get; }

    private AVTransportService AvTransport => _device.AVTransportService;

    private RenderingControlService RenderingControl => _device.RenderingControlService;

    private GroupRenderingControlService GroupRenderingControl => _device.GroupRenderingControlService;

    internal async Task<SonosDeviceDescription> ReadDescriptionAsync(CancellationToken cancellationToken)
    {
        var xml = await _httpClient.GetStringAsync(new Uri(BaseUri, "/xml/device_description.xml"), cancellationToken);
        return DeviceDescriptionParser.Parse(xml);
    }

    internal async Task<SonosZoneInfo> ReadZoneInfoAsync(CancellationToken cancellationToken)
    {
        var zoneInfo = await _device.DevicePropertiesService.GetZoneInfo(cancellationToken);
        return new SonosZoneInfo(zoneInfo.SerialNumber, zoneInfo.MACAddress, zoneInfo.HardwareVersion, zoneInfo.DisplaySoftwareVersion);
    }

    internal async Task<SonosTopology> ReadTopologyAsync(CancellationToken cancellationToken)
    {
        var response = await _device.ZoneGroupTopologyService.GetZoneGroupState(cancellationToken);
        return ZoneGroupStateParser.Parse(response.ZoneGroupState);
    }

    internal async Task<IReadOnlyList<SonosFavorite>> ReadFavoritesAsync(CancellationToken cancellationToken)
    {
        var response = await _device.ContentDirectoryService.Browse("FV:2", Count: 100, cancellationToken: cancellationToken);
        return FavoritesParser.Parse(response.Result);
    }

    /// <summary>
    /// Reads one player. Requests go one after another: a poll is a dozen small calls, and players are polled in
    /// parallel already.
    /// </summary>
    internal async Task<SonosPlayerReading> ReadPlayerAsync(bool isHomeTheater, CancellationToken cancellationToken)
    {
        var transport = await AvTransport.GetTransportInfo(cancellationToken);
        var settings = await AvTransport.GetTransportSettings(cancellationToken);
        var media = await AvTransport.GetMediaInfo(cancellationToken);
        var position = await AvTransport.GetPositionInfo(cancellationToken);
        var sleepTimer = await AvTransport.GetRemainingSleepTimerDuration(cancellationToken);

        var volume = await RenderingControl.GetVolume(new RenderingControlService.GetVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken);
        var mute = await RenderingControl.GetMute(new RenderingControlService.GetMuteRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken);
        var bass = await RenderingControl.GetBass(cancellationToken);
        var treble = await RenderingControl.GetTreble(cancellationToken);
        var loudness = await RenderingControl.GetLoudness(new RenderingControlService.GetLoudnessRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken);

        bool? nightMode = null;
        bool? speechEnhancement = null;
        if (isHomeTheater)
        {
            nightMode = await GetEqualizerAsync("NightMode", cancellationToken);
            speechEnhancement = await GetEqualizerAsync("DialogLevel", cancellationToken);
        }

        return new SonosPlayerReading(
            new AvTransportChange(
                transport.CurrentTransportState,
                settings.PlayMode,
                media.CurrentURI,
                position.TrackURI,
                position.TrackDuration,
                position.TrackMetaData),
            SonosValues.ParseDuration(position.RelTime),
            SonosValues.ParseDuration(sleepTimer.RemainingSleepTimerDuration),
            new RenderingControlChange(
                volume.CurrentVolume,
                mute.CurrentMute,
                bass.CurrentBass,
                treble.CurrentTreble,
                loudness.CurrentLoudness,
                nightMode,
                speechEnhancement));
    }

    internal async Task<GroupRenderingControlChange> ReadGroupAsync(CancellationToken cancellationToken)
    {
        var volume = await GroupRenderingControl.GetGroupVolume(cancellationToken);
        var mute = await GroupRenderingControl.GetGroupMute(cancellationToken);
        return new GroupRenderingControlChange(volume.CurrentVolume, mute.CurrentMute);
    }

    internal Task PlayAsync(CancellationToken cancellationToken) => _device.Play(cancellationToken);

    internal Task PauseAsync(CancellationToken cancellationToken) => _device.Pause(cancellationToken);

    internal Task StopAsync(CancellationToken cancellationToken) => _device.Stop(cancellationToken);

    internal Task NextAsync(CancellationToken cancellationToken) => _device.Next(cancellationToken);

    internal Task PreviousAsync(CancellationToken cancellationToken) => _device.Previous(cancellationToken);

    internal Task TogglePlaybackAsync(CancellationToken cancellationToken) => _device.TogglePlayback(cancellationToken);

    internal Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) =>
        AvTransport.Seek(new AVTransportService.SeekRequest { InstanceID = InstanceId, Unit = "REL_TIME", Target = SonosValues.FormatDuration(position) }, cancellationToken);

    internal Task SetVolumeAsync(int volume, CancellationToken cancellationToken) =>
        RenderingControl.SetVolume(new RenderingControlService.SetVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel, DesiredVolume = volume }, cancellationToken);

    internal Task ChangeVolumeAsync(int adjustment, CancellationToken cancellationToken) =>
        RenderingControl.SetRelativeVolume(new RenderingControlService.SetRelativeVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel, Adjustment = adjustment }, cancellationToken);

    internal Task RampVolumeAsync(int volume, CancellationToken cancellationToken) =>
        RenderingControl.RampToVolume(new RenderingControlService.RampToVolumeRequest
        {
            InstanceID = InstanceId,
            Channel = MasterChannel,
            RampType = "SLEEP_TIMER_RAMP_TYPE",
            DesiredVolume = volume,
            ResetVolumeAfter = false,
            ProgramURI = string.Empty
        }, cancellationToken);

    internal Task SetMuteAsync(bool mute, CancellationToken cancellationToken) =>
        RenderingControl.SetMute(new RenderingControlService.SetMuteRequest { InstanceID = InstanceId, Channel = MasterChannel, DesiredMute = mute }, cancellationToken);

    internal Task SetTransportUriAsync(string uri, string metadata, CancellationToken cancellationToken) =>
        AvTransport.SetAVTransportURI(new AVTransportService.SetAVTransportURIRequest { InstanceID = InstanceId, CurrentURI = uri, CurrentURIMetaData = metadata }, cancellationToken);

    internal async Task PlayFromQueueAsync(string uri, string metadata, CancellationToken cancellationToken)
    {
        await AvTransport.RemoveAllTracksFromQueue(cancellationToken);
        await AvTransport.AddURIToQueue(new AVTransportService.AddURIToQueueRequest
        {
            InstanceID = InstanceId,
            EnqueuedURI = uri,
            EnqueuedURIMetaData = metadata,
            DesiredFirstTrackNumberEnqueued = 0,
            EnqueueAsNext = false
        }, cancellationToken);
        await _device.SwitchToQueue(cancellationToken);
        await _device.Play(cancellationToken);
    }

    internal Task<bool> PlayNotificationAsync(Uri soundUri, int volume, CancellationToken cancellationToken) =>
        _device.QueueNotification(new NotificationOptions(soundUri, volume), cancellationToken);

    internal Task SwitchToTvAsync(CancellationToken cancellationToken) => _device.SwitchToSpdif(cancellationToken);

    internal Task SwitchToLineInAsync(CancellationToken cancellationToken) => _device.SwitchToLineIn(cancellationToken);

    internal Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken) =>
        AvTransport.SetPlayMode(new AVTransportService.SetPlayModeRequest { InstanceID = InstanceId, NewPlayMode = playMode }, cancellationToken);

    internal Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        AvTransport.ConfigureSleepTimer(new AVTransportService.ConfigureSleepTimerRequest
        {
            InstanceID = InstanceId,
            NewSleepTimerDuration = duration <= TimeSpan.Zero ? string.Empty : SonosValues.FormatDuration(duration)
        }, cancellationToken);

    internal Task SetBassAsync(int bass, CancellationToken cancellationToken) =>
        RenderingControl.SetBass(new RenderingControlService.SetBassRequest { InstanceID = InstanceId, DesiredBass = bass }, cancellationToken);

    internal Task SetTrebleAsync(int treble, CancellationToken cancellationToken) =>
        RenderingControl.SetTreble(new RenderingControlService.SetTrebleRequest { InstanceID = InstanceId, DesiredTreble = treble }, cancellationToken);

    internal Task SetLoudnessAsync(bool loudness, CancellationToken cancellationToken) =>
        RenderingControl.SetLoudness(new RenderingControlService.SetLoudnessRequest { InstanceID = InstanceId, Channel = MasterChannel, DesiredLoudness = loudness }, cancellationToken);

    internal Task SetEqualizerAsync(string type, bool enabled, CancellationToken cancellationToken) =>
        RenderingControl.SetEQ(new RenderingControlService.SetEQRequest { InstanceID = InstanceId, EQType = type, DesiredValue = enabled ? 1 : 0 }, cancellationToken);

    internal Task JoinAsync(string coordinatorUuid, CancellationToken cancellationToken) =>
        SetTransportUriAsync($"x-rincon:{coordinatorUuid}", string.Empty, cancellationToken);

    internal Task LeaveGroupAsync(CancellationToken cancellationToken) =>
        AvTransport.BecomeCoordinatorOfStandaloneGroup(cancellationToken);

    internal async Task SetGroupVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        // The snapshot is what makes Sonos keep the volume ratio between the members.
        await GroupRenderingControl.SnapshotGroupVolume(cancellationToken);
        await GroupRenderingControl.SetGroupVolume(new GroupRenderingControlService.SetGroupVolumeRequest { InstanceID = InstanceId, DesiredVolume = volume }, cancellationToken);
    }

    internal Task ChangeGroupVolumeAsync(int adjustment, CancellationToken cancellationToken) =>
        GroupRenderingControl.SetRelativeGroupVolume(new GroupRenderingControlService.SetRelativeGroupVolumeRequest { InstanceID = InstanceId, Adjustment = adjustment }, cancellationToken);

    internal Task SetGroupMuteAsync(bool mute, CancellationToken cancellationToken) =>
        GroupRenderingControl.SetGroupMute(new GroupRenderingControlService.SetGroupMuteRequest { InstanceID = InstanceId, DesiredMute = mute }, cancellationToken);

    private async Task<bool> GetEqualizerAsync(string type, CancellationToken cancellationToken)
    {
        var response = await RenderingControl.GetEQ(new RenderingControlService.GetEQRequest { InstanceID = InstanceId, EQType = type }, cancellationToken);
        return response.CurrentValue == 1;
    }

    public void Dispose() => _device.Dispose();
}
```

`Sonos.Base` nests its request types inside the service classes (`RenderingControlService.SetVolumeRequest`). If a type name differs in 0.4.0, look it up with `grep -n "class <Name>" ~/.nuget/packages/sonos.base/0.4.0/lib/net10.0/Sonos.Base.xml` and adjust; the property names in this file were verified against the 0.4.0 source.

- [ ] **Step 6: Implement `SonosDiscovery.cs`**

```csharp
using System.Net;
using System.Net.Sockets;
using Rssdp;

namespace Namotion.Devices.Sonos.Client;

internal static class SonosDiscovery
{
    private const string ZonePlayerSearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Builds a speaker URI from <c>host</c> or <c>host:port</c>; the port defaults to the Sonos port 1400.
    /// </summary>
    internal static Uri CreateDeviceUri(string host)
    {
        var uri = new Uri("http://" + host.Trim());
        return new UriBuilder("http", uri.Host, uri.IsDefaultPort ? SonosValues.DevicePort : uri.Port).Uri;
    }

    /// <summary>
    /// Returns the local address the OS would use to reach the host, or null when no route exists.
    /// </summary>
    internal static string? DetectLocalAddress(string remoteHost)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Connecting a UDP socket sends nothing; it only makes the OS choose the route and its local address.
            socket.Connect(remoteHost, SonosValues.DevicePort);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Searches all IPv4 interfaces for any Sonos speaker and returns its base URI.
    /// </summary>
    internal static async Task<Uri?> FindSpeakerAsync(CancellationToken cancellationToken)
    {
        using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
        var devices = await locator.SearchAsync(ZonePlayerSearchTarget, SearchTime, cancellationToken);
        var location = devices.Select(device => device.DescriptionLocation).FirstOrDefault(uri => uri is not null);
        return location is null ? null : new UriBuilder("http", location.Host, location.Port).Uri;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~SonosConnectionTests|FullyQualifiedName~SonosDiscoveryTests"`
Expected: all pass. If `WhenSetVolumeIsRejected_ThenThrows` sees a different exception type (Sonos.Base may wrap the connection failure), assert on that type instead; the requirement is that a failed command throws.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos/Client src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: talk to Sonos speakers through Sonos.Base and find them over SSDP

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: SonosSystem runtime (connect, poll, subscribe, tear down) and DI extension

**Files:**
- Replace: `src/HomeBlaze/Namotion.Devices.Sonos/SonosSystem.Runtime.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos/SonosServiceCollectionExtensions.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/ConnectedSystem.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosSystemRuntimeTests.cs`, `SonosServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2 to 8.
- Produces (internal on `SonosSystem`): `SonosConnection GetConnectionForCommand(string uuid)`, `Task ReconcileAsync(CancellationToken)`, `Task RefreshAfterCommandAsync(SonosPlayer player, CancellationToken)`; public `ApplyConfigurationAsync`, `Dispose`. `SonosServiceCollectionExtensions.AddSonos(this IServiceCollection, Action<SonosSystem>? configure = null, Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)`.
- Produces (tests): `ConnectedSystem.StartAsync(FakeSonosSpeaker speaker, string uuid = TestFixtures.KitchenUuid, string room = "Küche")` returning `ConnectedSystem` with `SonosSystem System`, `SonosPlayer Player`, `IAsyncDisposable`.

- [ ] **Step 1: Write the connected system helper**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/Testing/ConnectedSystem.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A running SonosSystem connected to a FakeSonosSpeaker, with events delivered over loopback.
/// </summary>
internal sealed class ConnectedSystem : IAsyncDisposable
{
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private ConnectedSystem(SonosSystem system, SonosPlayer player)
    {
        System = system;
        Player = player;
    }

    internal SonosSystem System { get; }

    internal SonosPlayer Player { get; }

    internal static SonosSystem CreateSystem(string seedHost) =>
        new(new TestHttpClientFactory(), NullLogger<SonosSystem>.Instance)
        {
            SeedHost = seedHost,
            EventCallbackHost = "127.0.0.1",
            EventPort = LoopbackHttpServer.GetFreePort(),
            RetryInterval = TimeSpan.FromSeconds(1)
        };

    internal static async Task<ConnectedSystem> StartAsync(
        FakeSonosSpeaker speaker, string uuid = TestFixtures.KitchenUuid, string room = "Küche")
    {
        speaker.RespondAsIdlePlayer(uuid, room);
        var system = CreateSystem(speaker.Host);
        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.TryGetValue(uuid, out var player) && player.Model is not null && system.AreEventsActive,
            WaitTimeout,
            message: "The system should connect to the fake speaker and subscribe to its events.");

        return new ConnectedSystem(system, system.Players[uuid]);
    }

    public async ValueTask DisposeAsync()
    {
        await System.StopAsync(CancellationToken.None);
        System.Dispose();
    }
}
```

- [ ] **Step 2: Write the failing runtime tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosSystemRuntimeTests.cs`:

```csharp
using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemRuntimeTests
{
    private const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";

    [Fact]
    public async Task WhenSeedHostAnswers_ThenSystemConnectsAndReadsThePlayer()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();

        // Act
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Assert
        var system = connected.System;
        var player = connected.Player;
        Assert.Equal(ServiceStatus.Running, system.Status);
        Assert.Equal("127.0.0.1", system.ActiveEventCallbackHost);
        Assert.Equal("Küche", player.RoomName);
        Assert.Equal("Sonos Ray", player.Model);
        Assert.Equal("S36", player.ProductCode);
        Assert.Equal("00-00-00-00-00-06:D", player.SerialNumber);
        Assert.Equal("18.8", player.SoftwareVersion);
        Assert.Equal(0.44m, player.Volume);
        Assert.Equal(SonosTransportState.Paused, player.TransportState);
        Assert.Equal(SonosSource.SpotifyConnect, player.Source);
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, system.Favorites);
        Assert.Equal(0.44m, Assert.Single(system.Groups).Value.Volume);
        Assert.NotNull(system.LastUpdated);
    }

    [Fact]
    public async Task WhenSpeakerSendsAvTransportEvent_ThenPlayerUpdatesWithoutWaitingForThePoll()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var callback = speaker.GetCallback(AvTransportEventPath)!;
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
        {
            Content = new StringContent(SonosEventBodies.AvTransport(("TransportState", "PLAYING")))
        };
        request.Headers.TryAddWithoutValidation("SID", FakeSonosSpeaker.SidFor(AvTransportEventPath));

        // Act
        using var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AsyncTestHelpers.WaitUntilAsync(
            () => connected.Player.TransportState == SonosTransportState.Playing,
            ConnectedSystem.WaitTimeout,
            message: "The event should reach the player.");
    }

    [Fact]
    public async Task WhenSeedHostIsUnreachable_ThenStatusIsError()
    {
        // Arrange
        var system = ConnectedSystem.CreateSystem($"127.0.0.1:{LoopbackHttpServer.GetFreePort()}");

        // Act
        await system.StartAsync(CancellationToken.None);

        // Assert
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.Status == ServiceStatus.Error,
                ConnectedSystem.WaitTimeout,
                message: "An unreachable seed should report an error.");
            Assert.False(system.IsConnected);
            Assert.NotNull(system.StatusMessage);
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    [Fact]
    public async Task WhenStopped_ThenSubscriptionsAreCancelledAndStatusIsStopped()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.System.StopAsync(CancellationToken.None);

        // Assert
        Assert.Contains(AvTransportEventPath, speaker.Unsubscribed);
        Assert.Equal(ServiceStatus.Stopped, connected.System.Status);
        Assert.False(connected.System.IsConnected);
        Assert.False(connected.Player.IsConnected);
        connected.System.Dispose();
    }

    [Fact]
    public async Task WhenConfigurationChanges_ThenSystemReconnects()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var initialTopologyReads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await connected.System.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.Calls.Count(call => call.Action == "GetZoneGroupState") > initialTopologyReads && connected.System.IsConnected,
            ConnectedSystem.WaitTimeout,
            message: "A configuration change should rebuild the connection.");
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddSonos_ThenConfigureIsApplied()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddLogging();

        // Act
        services.AddSonos(system => system.SeedHost = "10.0.0.121");
        using var serviceProvider = services.BuildServiceProvider();

        // Assert
        var system = serviceProvider.GetRequiredService<SonosSystem>();
        Assert.Equal("10.0.0.121", system.SeedHost);
        Assert.Equal(6329, system.EventPort);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~SonosSystemRuntimeTests|FullyQualifiedName~SonosServiceCollectionExtensionsTests"`
Expected: build fails (`AddSonos` missing); after adding it, runtime tests time out because the temporary runtime never connects.

- [ ] **Step 4: Replace `SonosSystem.Runtime.cs`**

```csharp
using System.Net;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Events;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    private const int MaxConsecutiveReconcileFailures = 3;
    private const string AvTransportService = "AVTransport";
    private const string RenderingControlService = "RenderingControl";
    private const string GroupRenderingControlService = "GroupRenderingControl";
    private const string TopologySubscriptionKey = "seed/ZoneGroupTopology";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnsubscribeTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly Lock _connectionsLock = new();
    private readonly Dictionary<string, SonosConnection> _connections = new(StringComparer.Ordinal);

    // Guarded by _connectionsLock and replaced for every connection attempt.
    private HttpClient? _httpClient;
    private SonosClientProvider? _clientProvider;
    private SonosEventListener? _eventListener;
    private SonosConnection? _seedConnection;
    private bool _disposed;

    private TimeSpan EffectivePollingInterval => PollingInterval > TimeSpan.Zero ? PollingInterval : DefaultPollingInterval;

    private TimeSpan EffectiveRetryInterval => RetryInterval > TimeSpan.Zero ? RetryInterval : DefaultRetryInterval;

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_configurationChanged.CurrentCount == 0)
        {
            _configurationChanged.Release();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var reconnectImmediately = false;
            Status = ServiceStatus.Starting;
            StatusMessage = null;

            try
            {
                OpenConnectionScope();
                var seedUri = await FindSeedAsync(stoppingToken)
                    ?? throw new InvalidOperationException(
                        "No Sonos speaker found. Set SeedHost when multicast discovery is blocked, for example under Docker bridge networking.");

                StartEventListener(seedUri.Host);
                reconnectImmediately = await RunConnectedAsync(seedUri, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Sonos system connection failed.");
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
            }
            finally
            {
                await CloseConnectionScopeAsync();
            }

            if (!reconnectImmediately && !await WaitForRetryAsync(stoppingToken))
            {
                break;
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Returns the connection for a command, refusing when the system or the player is not connected: a command
    /// would physically succeed, but nothing would be polling or subscribed to show its result.
    /// </summary>
    internal SonosConnection GetConnectionForCommand(string uuid)
    {
        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "The Sonos system is not connected. " + (StatusMessage ?? "Waiting for the connection to be established."));
            }

            if (!Players.TryGetValue(uuid, out var player) || !player.IsConnected || !_connections.TryGetValue(uuid, out var connection))
            {
                throw new InvalidOperationException($"The Sonos player {uuid} is not connected.");
            }

            return connection;
        }
    }

    /// <summary>
    /// Re-reads the players of the commanded player's group so a command's effect shows without events.
    /// </summary>
    internal async Task RefreshAfterCommandAsync(SonosPlayer player, CancellationToken cancellationToken)
    {
        var coordinatorUuid = player.GroupCoordinatorUuid ?? player.Uuid;
        var pollStartedAt = TimeProvider.System.GetUtcNow();
        var groupPlayers = Players.Values
            .Where(candidate => candidate.IsConnected && (candidate.GroupCoordinatorUuid ?? candidate.Uuid) == coordinatorUuid)
            .ToArray();

        await Task.WhenAll(groupPlayers.Select(candidate => PollPlayerAsync(candidate, pollStartedAt, cancellationToken)));
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var seedConnection = GetSeedConnection();
            var topology = await seedConnection.ReadTopologyAsync(cancellationToken);
            ApplyTopology(topology);
            SyncConnections();

            var pollStartedAt = TimeProvider.System.GetUtcNow();
            var players = Players.Values.Where(player => player.IsInTopology).ToArray();
            await Task.WhenAll(players.Select(player => PollPlayerAsync(player, pollStartedAt, cancellationToken)));
            await Task.WhenAll(players
                .SelectMany(player => player.Satellites.Values)
                .Where(satellite => satellite.IsInTopology && satellite.NeedsStaticData)
                .Select(satellite => PollSatelliteAsync(satellite, cancellationToken)));

            await RefreshFavoritesAsync(seedConnection, cancellationToken);
            await EnsureSubscriptionsAsync(cancellationToken);
            LastUpdated = DateTimeOffset.Now;
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void OpenConnectionScope()
    {
        var httpClient = HttpClientFactory.CreateClient(nameof(SonosSystem));
        httpClient.Timeout = RequestTimeout;

        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _httpClient = httpClient;
            _clientProvider = new SonosClientProvider(httpClient);
            _eventListener = new SonosEventListener(httpClient, _logger);
        }
    }

    private async Task CloseConnectionScopeAsync()
    {
        IsConnected = false;
        AreEventsActive = false;
        ActiveEventCallbackHost = null;

        SonosEventListener? eventListener;
        HttpClient? httpClient;
        List<SonosConnection> connections;
        lock (_connectionsLock)
        {
            eventListener = _eventListener;
            httpClient = _httpClient;
            connections = [.. _connections.Values];
            if (_seedConnection is not null)
            {
                connections.Add(_seedConnection);
            }

            _connections.Clear();
            _seedConnection = null;
            _eventListener = null;
            _clientProvider = null;
            _httpClient = null;
        }

        if (eventListener is not null)
        {
            // The stopping token is already cancelled on shutdown, so unsubscribing gets its own short budget.
            using var unsubscribeCancellation = new CancellationTokenSource(UnsubscribeTimeout);
            await eventListener.UnsubscribeAllAsync(unsubscribeCancellation.Token);
            await eventListener.DisposeAsync();
        }

        foreach (var connection in connections)
        {
            connection.Dispose();
        }

        httpClient?.Dispose();

        foreach (var player in Players.Values)
        {
            player.ReportPollFailed("The Sonos system is disconnected.");
            foreach (var satellite in player.Satellites.Values)
            {
                satellite.ReportPollFailed("The Sonos system is disconnected.");
            }
        }
    }

    private async Task<bool> WaitForRetryAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _configurationChanged.WaitAsync(EffectiveRetryInterval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<Uri?> FindSeedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(SeedHost))
        {
            return SonosDiscovery.CreateDeviceUri(SeedHost);
        }

        var knownPlayer = Players.Values.FirstOrDefault(player => player.BaseUri is not null);
        if (knownPlayer is not null)
        {
            return knownPlayer.BaseUri;
        }

        return await SonosDiscovery.FindSpeakerAsync(cancellationToken);
    }

    private void StartEventListener(string seedHost)
    {
        var callbackHost = string.IsNullOrWhiteSpace(EventCallbackHost)
            ? SonosDiscovery.DetectLocalAddress(seedHost)
            : EventCallbackHost.Trim();

        if (callbackHost is null)
        {
            _logger.LogWarning("No local address routes to the Sonos speaker {Host}; continuing with polling only.", seedHost);
            return;
        }

        try
        {
            _eventListener!.Start(callbackHost, EventPort);
            ActiveEventCallbackHost = callbackHost;
        }
        catch (HttpListenerException exception)
        {
            _logger.LogWarning(exception, "The Sonos event listener could not listen on port {Port}; continuing with polling only.", EventPort);
        }
    }

    private async Task<bool> RunConnectedAsync(Uri seedUri, CancellationToken stoppingToken)
    {
        SetSeed(seedUri);

        // The first reconciliation is what establishes the connection, so its failure ends this attempt.
        await ReconcileAsync(stoppingToken);
        MarkConnected();

        var consecutiveFailures = 0;
        while (true)
        {
            if (await _configurationChanged.WaitAsync(EffectivePollingInterval, stoppingToken))
            {
                return true;
            }

            try
            {
                await ReconcileAsync(stoppingToken);
                consecutiveFailures = 0;
                MarkConnected();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveReconcileFailures)
                {
                    throw;
                }

                _logger.LogWarning(exception,
                    "Sonos reconciliation failed ({FailureCount} of {MaxFailureCount}); trying another speaker as seed.",
                    consecutiveFailures, MaxConsecutiveReconcileFailures);
                StatusMessage = $"Reconciliation failed ({consecutiveFailures} of {MaxConsecutiveReconcileFailures}): {exception.Message}";
                SetSeed(SelectNextSeed());
            }
        }
    }

    private void MarkConnected()
    {
        IsConnected = true;
        Status = ServiceStatus.Running;
        StatusMessage = null;
    }

    private void SetSeed(Uri seedUri)
    {
        lock (_connectionsLock)
        {
            if (_seedConnection?.BaseUri == seedUri)
            {
                return;
            }

            _seedConnection?.Dispose();
            _seedConnection = new SonosConnection(seedUri, null, _httpClient!, _clientProvider!);
        }
    }

    private SonosConnection GetSeedConnection()
    {
        lock (_connectionsLock)
        {
            return _seedConnection ?? throw new InvalidOperationException("No Sonos seed speaker is selected.");
        }
    }

    private Uri? GetSeedUri()
    {
        lock (_connectionsLock)
        {
            return _seedConnection?.BaseUri;
        }
    }

    private Uri SelectNextSeed()
    {
        var current = GetSeedUri();
        return Players.Values
            .Where(player => player.IsConnected && player.BaseUri is not null && player.BaseUri != current)
            .Select(player => player.BaseUri!)
            .FirstOrDefault() ?? current!;
    }

    private void SyncConnections()
    {
        lock (_connectionsLock)
        {
            if (_httpClient is null || _clientProvider is null)
            {
                return;
            }

            foreach (var player in Players.Values)
            {
                SyncConnection(player);
                foreach (var satellite in player.Satellites.Values)
                {
                    SyncConnection(satellite);
                }
            }
        }
    }

    // Caller holds _connectionsLock.
    private void SyncConnection(SonosDevice device)
    {
        if (!device.IsInTopology || device.BaseUri is not { } baseUri)
        {
            return;
        }

        if (_connections.TryGetValue(device.Uuid, out var existing))
        {
            if (existing.BaseUri == baseUri)
            {
                return;
            }

            existing.Dispose();
        }

        _connections[device.Uuid] = new SonosConnection(baseUri, device.Uuid, _httpClient!, _clientProvider!);
        device.InvalidateStaticData();
    }

    private SonosConnection? FindConnection(string uuid)
    {
        lock (_connectionsLock)
        {
            return _connections.GetValueOrDefault(uuid);
        }
    }

    private async Task PollPlayerAsync(SonosPlayer player, DateTimeOffset pollStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            var connection = FindConnection(player.Uuid)
                ?? throw new InvalidOperationException("No connection to the player.");

            if (player.NeedsStaticData)
            {
                await ReadStaticDataAsync(player, connection, cancellationToken);
            }

            var reading = await connection.ReadPlayerAsync(player.IsHomeTheater, cancellationToken);
            player.ApplyPoll(reading, pollStartedAt);

            if (Groups.TryGetValue(player.Uuid, out var group))
            {
                group.ApplyGroupRenderingControlPoll(await connection.ReadGroupAsync(cancellationToken), pollStartedAt);
            }

            player.ReportPollSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (player.ReportPollFailed(exception.Message))
            {
                _logger.LogWarning(exception, "Polling the Sonos player in {Room} failed.", player.RoomName);
            }
        }
    }

    private async Task PollSatelliteAsync(SonosSatellite satellite, CancellationToken cancellationToken)
    {
        try
        {
            var connection = FindConnection(satellite.Uuid)
                ?? throw new InvalidOperationException("No connection to the satellite.");

            await ReadStaticDataAsync(satellite, connection, cancellationToken);
            satellite.ReportPollSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (satellite.ReportPollFailed(exception.Message))
            {
                _logger.LogWarning(exception, "Reading the Sonos satellite {Uuid} failed.", satellite.Uuid);
            }
        }
    }

    private static async Task ReadStaticDataAsync(SonosDevice device, SonosConnection connection, CancellationToken cancellationToken)
    {
        var description = await connection.ReadDescriptionAsync(cancellationToken);
        var zoneInfo = await connection.ReadZoneInfoAsync(cancellationToken);
        device.ApplyStaticData(description, zoneInfo.SerialNumber, zoneInfo.MacAddress, zoneInfo.HardwareVersion, zoneInfo.DisplayVersion);
    }

    private async Task RefreshFavoritesAsync(SonosConnection seedConnection, CancellationToken cancellationToken)
    {
        try
        {
            SetFavorites(await seedConnection.ReadFavoritesAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reading the Sonos favorites failed.");
        }
    }

    private async Task EnsureSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var eventListener = _eventListener;
        if (eventListener is null || !eventListener.IsListening)
        {
            AreEventsActive = false;
            return;
        }

        var desired = GetDesiredSubscriptions();
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in eventListener.Subscriptions)
        {
            if (!desired.TryGetValue(subscription.Key, out var target) || target.EventUri != subscription.EventUri)
            {
                await RunSubscriptionRequestAsync(() => eventListener.UnsubscribeAsync(subscription, cancellationToken), subscription.Key, cancellationToken);
            }
            else if (subscription.Sid is not null && subscription.RenewAt <= now)
            {
                await RunSubscriptionRequestAsync(() => eventListener.RenewAsync(subscription, cancellationToken), subscription.Key, cancellationToken);
            }
        }

        var active = eventListener.Subscriptions.Select(subscription => subscription.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, target) in desired)
        {
            if (!active.Contains(key))
            {
                await RunSubscriptionRequestAsync(() => eventListener.SubscribeAsync(key, target.EventUri, target.Handler, cancellationToken), key, cancellationToken);
            }
        }

        AreEventsActive = eventListener.Subscriptions.Any(subscription => subscription.Sid is not null);
    }

    private async Task RunSubscriptionRequestAsync(Func<Task> request, string key, CancellationToken cancellationToken)
    {
        try
        {
            await request();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The Sonos event subscription {Key} failed; polling keeps its state current.", key);
        }
    }

    private Dictionary<string, (Uri EventUri, Action<string> Handler)> GetDesiredSubscriptions()
    {
        var desired = new Dictionary<string, (Uri EventUri, Action<string> Handler)>(StringComparer.Ordinal);
        var groups = Groups;
        foreach (var player in Players.Values)
        {
            if (!player.IsConnected || player.BaseUri is not { } baseUri)
            {
                continue;
            }

            desired[$"{player.Uuid}/{AvTransportService}"] =
                (new Uri(baseUri, "/MediaRenderer/AVTransport/Event"), body => OnAvTransportEvent(player, body));
            desired[$"{player.Uuid}/{RenderingControlService}"] =
                (new Uri(baseUri, "/MediaRenderer/RenderingControl/Event"), body => OnRenderingControlEvent(player, body));

            if (groups.ContainsKey(player.Uuid))
            {
                var coordinatorUuid = player.Uuid;
                desired[$"{player.Uuid}/{GroupRenderingControlService}"] =
                    (new Uri(baseUri, "/MediaRenderer/GroupRenderingControl/Event"), body => OnGroupRenderingControlEvent(coordinatorUuid, body));
            }
        }

        if (GetSeedUri() is { } seedUri)
        {
            desired[TopologySubscriptionKey] = (new Uri(seedUri, "/ZoneGroupTopology/Event"), OnTopologyEvent);
        }

        return desired;
    }

    private static void OnAvTransportEvent(SonosPlayer player, string body) =>
        player.ApplyAvTransportEvent(UpnpEventParser.ParseAvTransport(body), TimeProvider.System.GetUtcNow());

    private static void OnRenderingControlEvent(SonosPlayer player, string body) =>
        player.ApplyRenderingControlEvent(UpnpEventParser.ParseRenderingControl(body), TimeProvider.System.GetUtcNow());

    private void OnGroupRenderingControlEvent(string coordinatorUuid, string body)
    {
        if (Groups.TryGetValue(coordinatorUuid, out var group))
        {
            group.ApplyGroupRenderingControlEvent(UpnpEventParser.ParseGroupRenderingControl(body), TimeProvider.System.GetUtcNow());
        }
    }

    // New players found here get their connection and subscriptions at the next reconciliation.
    private void OnTopologyEvent(string body)
    {
        var zoneGroupState = UpnpEventParser.ParseZoneGroupState(body);
        if (!string.IsNullOrEmpty(zoneGroupState))
        {
            ApplyTopology(ZoneGroupStateParser.Parse(zoneGroupState));
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Cancel first: base.Dispose() cancels the stopping token, so the loop cannot open another connection
        // after the flag below closes the door.
        base.Dispose();
        lock (_connectionsLock)
        {
            _disposed = true;
        }

        _configurationChanged.Dispose();
        _reconcileLock.Dispose();
    }
}
```

- [ ] **Step 5: Add the DI extension**

`src/HomeBlaze/Namotion.Devices.Sonos/SonosServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Sonos;

public static class SonosServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="SonosSystem"/> as a hosted subject.
    /// </summary>
    public static IServiceCollection AddSonos(
        this IServiceCollection services,
        Action<SonosSystem>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests`
Expected: all pass. If `WhenSeedHostAnswers` fails on `Favorites`, check that the fake's `Browse` envelope parses (Sonos.Base reads `Result` as text, so the escaped fixture must arrive unescaped once).

- [ ] **Step 7: Build the solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: connect, poll and subscribe to the Sonos household

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Operations and enabled states

**Files:**
- Modify: `src/HomeBlaze/Namotion.Devices.Sonos/SonosPlayer.cs`, `SonosGroup.cs`, `SonosSystem.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosPlayerOperationTests.cs`, `SonosGroupOperationTests.cs`, `SonosSystemOperationTests.cs`

**Interfaces:**
- Consumes: `SonosSystem.GetConnectionForCommand`, `RefreshAfterCommandAsync`, `ReconcileAsync`, `FindPlayer`, `FindFavorite`, `CreateUnknownRoomException` (Tasks 7, 9), `SonosConnection` commands (Task 8).
- Produces: `SonosPlayer` and `SonosGroup` implement `IAudioPlayer`; operations listed in the spec; `*_IsEnabled` derived properties.

- [ ] **Step 1: Write the failing tests**

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosPlayerOperationTests.cs`:

```csharp
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosPlayerOperationTests
{
    private static SonosPlayer CreateDisconnectedKitchen()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        return system.Players[TestFixtures.KitchenUuid];
    }

    [Fact]
    public async Task WhenSystemIsNotConnected_ThenPlayThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => player.PlayAsync(CancellationToken.None));
        Assert.Contains("not connected", exception.Message);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public async Task WhenBassIsOutOfRange_ThenThrows(int bass)
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetBassAsync(bass, CancellationToken.None));
    }

    [Fact]
    public async Task WhenJoiningUnknownRoom_ThenThrowsListingTheRooms()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => player.JoinGroupAsync("Nowhere", CancellationToken.None));
        Assert.Contains("Wohnzimmer", exception.Message);
    }

    [Fact]
    public async Task WhenPlayingUnknownFavorite_ThenThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => player.PlayFavoriteAsync("Nothing", CancellationToken.None));
    }

    [Fact]
    public async Task WhenNightModeOnPlayerWithoutHomeTheater_ThenThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.SetNightModeAsync(true, CancellationToken.None));
    }

    [Fact]
    public void WhenPlayerIsAloneAndHasNoDuration_ThenGroupAndSeekOperationsAreDisabled()
    {
        // Act
        var player = CreateDisconnectedKitchen();

        // Assert
        Assert.False(player.LeaveGroup_IsEnabled);
        Assert.False(player.Seek_IsEnabled);
        Assert.False(player.SetNightMode_IsEnabled);
        Assert.False(player.Play_IsEnabled);
    }

    [Fact]
    public async Task WhenSettingVolume_ThenSonosVolumeIsSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.SetVolumeAsync(0.5m, CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetVolume" && call.Body.Contains("<DesiredVolume>50</DesiredVolume>"));
        Assert.True(connected.Player.Play_IsEnabled);
    }

    [Fact]
    public async Task WhenPlayingStreamFavorite_ThenUriWithStoredMetadataIsSetAndPlayed()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayFavoriteAsync("radio fm1", CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToArray();
        var setUri = Array.FindIndex(calls, call => call.Action == "SetAVTransportURI" && call.Body.Contains("tunein%3a9557"));
        var play = Array.FindLastIndex(calls, call => call.Action == "Play");
        Assert.True(setUri >= 0, "SetAVTransportURI with the favorite URI was not sent.");
        Assert.True(play > setUri, "Play was not sent after setting the URI.");
        Assert.Contains("Radio FM1", calls[setUri].Body);
    }

    [Fact]
    public async Task WhenPlayingHttpUri_ThenRadioSchemeAndEscapedTitleAreSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayUriAsync("http://stream.example.com/live.mp3", "Rock & Roll", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        Assert.Contains("x-rincon-mp3radio://stream.example.com/live.mp3", setUri.Body);
        Assert.Contains("Rock &amp;amp; Roll", setUri.Body);
    }

    [Fact]
    public async Task WhenTogglingWhilePaused_ThenPlayIsSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.TogglePlaybackAsync(CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "Play");
        Assert.DoesNotContain(speaker.Calls, call => call.Action == "Pause");
    }

    [Fact]
    public async Task WhenSettingShuffle_ThenRepeatIsKept()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.SetShuffleAsync(true, CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetPlayMode" && call.Body.Contains("<NewPlayMode>SHUFFLE_NOREPEAT</NewPlayMode>"));
    }
}
```

The escaped title assertion: the DIDL metadata escapes `&` to `&amp;`, and the SOAP body escapes that string again, so the wire carries `&amp;amp;`.

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosGroupOperationTests.cs`:

```csharp
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupOperationTests
{
    [Fact]
    public async Task WhenSettingGroupVolume_ThenSnapshotPrecedesSetGroupVolume()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var group = Assert.Single(connected.System.Groups).Value;

        // Act
        await group.SetVolumeAsync(0.3m, CancellationToken.None);

        // Assert
        var actions = speaker.Calls.Select(call => call.Action).ToList();
        var snapshot = actions.IndexOf("SnapshotGroupVolume");
        var setVolume = actions.IndexOf("SetGroupVolume");
        Assert.True(snapshot >= 0 && setVolume > snapshot);
        Assert.Contains(speaker.Calls, call => call.Action == "SetGroupVolume" && call.Body.Contains("<DesiredVolume>30</DesiredVolume>"));
    }

    [Fact]
    public async Task WhenGroupSystemIsNotConnected_ThenPauseThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.PauseAsync(CancellationToken.None));
    }
}
```

`src/HomeBlaze/Namotion.Devices.Sonos.Tests/SonosSystemOperationTests.cs`:

```csharp
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemOperationTests
{
    [Fact]
    public async Task WhenGroupingAllIntoUnknownRoom_ThenThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => system.GroupAllAsync("Nowhere", CancellationToken.None));
    }

    [Fact]
    public async Task WhenRefreshingWhileDisconnected_ThenThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenRefreshing_ThenTopologyIsReadAgain()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var reads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await connected.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.True(speaker.Calls.Count(call => call.Action == "GetZoneGroupState") > reads);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests --filter "FullyQualifiedName~OperationTests"`
Expected: build fails, the operations do not exist.

- [ ] **Step 3: Add the player operations**

In `SonosPlayer.cs`, change the class declaration to:

```csharp
public partial class SonosPlayer : SonosDevice,
    IAudioPlayer,
    IMediaTrackState,
    IBatteryState
```

Add this using at the top:

```csharp
using Namotion.Devices.Sonos.Client;
```

Add these members before `ApplyPlayerTopology` (the `*_IsEnabled` properties already exist from Task 7):

```csharp
    [Operation(Position = 1)]
    public Task PlayAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PlayAsync(token), cancellationToken);

    [Operation(Position = 2)]
    public Task PauseAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PauseAsync(token), cancellationToken);

    [Operation(Position = 3)]
    public Task StopAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.StopAsync(token), cancellationToken);

    [Operation(Position = 4)]
    public Task NextAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.NextAsync(token), cancellationToken);

    [Operation(Position = 5)]
    public Task PreviousAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PreviousAsync(token), cancellationToken);

    [Operation(Position = 6)]
    public Task TogglePlaybackAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.TogglePlaybackAsync(token), cancellationToken);

    [Operation(Position = 7)]
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return RunOnCoordinatorAsync((connection, token) => connection.SeekAsync(position, token), cancellationToken);
    }

    [Operation(Position = 10)]
    public Task SetVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetVolumeAsync(SonosValues.ToSonosVolume(volume), token), cancellationToken);

    [Operation(Position = 11)]
    public Task ChangeVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal delta, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.ChangeVolumeAsync(SonosValues.ToSonosVolumeAdjustment(delta), token), cancellationToken);

    [Operation(Position = 12)]
    public Task RampVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.RampVolumeAsync(SonosValues.ToSonosVolume(volume), token), cancellationToken);

    [Operation(Position = 13)]
    public Task MuteAsync(CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetMuteAsync(true, token), cancellationToken);

    [Operation(Position = 14)]
    public Task UnmuteAsync(CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetMuteAsync(false, token), cancellationToken);

    [Operation(Position = 20)]
    public Task PlayFavoriteAsync(string name, CancellationToken cancellationToken)
    {
        var favorite = _system.FindFavorite(name)
            ?? throw new ArgumentException(
                $"Unknown Sonos favorite '{name}'. Known favorites: {string.Join(", ", _system.Favorites)}.", nameof(name));

        return RunOnCoordinatorAsync(async (connection, token) =>
        {
            if (favorite.IsContainer)
            {
                await connection.PlayFromQueueAsync(favorite.Uri, favorite.Metadata, token);
            }
            else
            {
                await connection.SetTransportUriAsync(favorite.Uri, favorite.Metadata, token);
                await connection.PlayAsync(token);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Plays a URI: http(s) streams play as radio with the optional title; native Sonos URIs pass through.
    /// </summary>
    [Operation(Position = 21)]
    public Task PlayUriAsync(string uri, string? title, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var isStream = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var transportUri = isStream ? SonosValues.ToStreamUri(uri) : uri;
        var metadata = isStream || title is not null ? SonosValues.CreateStreamMetadata(title ?? uri) : string.Empty;

        return RunOnCoordinatorAsync(async (connection, token) =>
        {
            await connection.SetTransportUriAsync(transportUri, metadata, token);
            await connection.PlayAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// Plays a sound over the current playback, which resumes afterwards. Needs S2 speakers.
    /// </summary>
    [Operation(Position = 22)]
    public Task PlayNotificationAsync(string soundUri, [OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(soundUri, UriKind.Absolute, out var sound) || (sound.Scheme != Uri.UriSchemeHttp && sound.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The sound URI must be an absolute http or https URI.", nameof(soundUri));
        }

        var sonosVolume = Math.Clamp(SonosValues.ToSonosVolume(volume), 1, 100);
        return RunOnPlayerAsync((connection, token) => connection.PlayNotificationAsync(sound, sonosVolume, token), cancellationToken);
    }

    [Operation(Position = 23)]
    public Task SwitchToTvAsync(CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SwitchToTvAsync(token), cancellationToken);
    }

    [Operation(Position = 24)]
    public Task SwitchToLineInAsync(CancellationToken cancellationToken)
    {
        if (!HasLineIn)
        {
            throw new InvalidOperationException($"{Title} has no line-in.");
        }

        return RunOnPlayerAsync((connection, token) => connection.SwitchToLineInAsync(token), cancellationToken);
    }

    [Operation(Position = 30)]
    public Task SetShuffleAsync(bool shuffle, CancellationToken cancellationToken)
    {
        var playMode = SonosValues.FormatPlayMode(shuffle, Repeat ?? SonosRepeatMode.Off);
        return RunOnCoordinatorAsync((connection, token) => connection.SetPlayModeAsync(playMode, token), cancellationToken);
    }

    [Operation(Position = 31)]
    public Task SetRepeatAsync(SonosRepeatMode repeat, CancellationToken cancellationToken)
    {
        var playMode = SonosValues.FormatPlayMode(Shuffle ?? false, repeat);
        return RunOnCoordinatorAsync((connection, token) => connection.SetPlayModeAsync(playMode, token), cancellationToken);
    }

    /// <summary>
    /// Sets the sleep timer; zero cancels it.
    /// </summary>
    [Operation(Position = 32)]
    public Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        return RunOnCoordinatorAsync((connection, token) => connection.SetSleepTimerAsync(duration, token), cancellationToken);
    }

    [Operation(Position = 40)]
    public Task SetBassAsync(int bass, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bass, -10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bass, 10);
        return RunOnPlayerAsync((connection, token) => connection.SetBassAsync(bass, token), cancellationToken);
    }

    [Operation(Position = 41)]
    public Task SetTrebleAsync(int treble, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(treble, -10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(treble, 10);
        return RunOnPlayerAsync((connection, token) => connection.SetTrebleAsync(treble, token), cancellationToken);
    }

    [Operation(Position = 42)]
    public Task SetLoudnessAsync(bool loudness, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetLoudnessAsync(loudness, token), cancellationToken);

    [Operation(Position = 43)]
    public Task SetNightModeAsync(bool nightMode, CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("NightMode", nightMode, token), cancellationToken);
    }

    [Operation(Position = 44)]
    public Task SetSpeechEnhancementAsync(bool speechEnhancement, CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("DialogLevel", speechEnhancement, token), cancellationToken);
    }

    [Operation(Position = 50)]
    public async Task JoinGroupAsync(string roomNameOrUuid, CancellationToken cancellationToken)
    {
        var target = _system.FindPlayer(roomNameOrUuid)
            ?? throw _system.CreateUnknownRoomException(roomNameOrUuid, nameof(roomNameOrUuid));
        if (ReferenceEquals(target, this))
        {
            throw new ArgumentException("A player cannot join its own group.", nameof(roomNameOrUuid));
        }

        await _system.GetConnectionForCommand(Uuid).JoinAsync(target.GroupCoordinatorUuid ?? target.Uuid, cancellationToken);
        await _system.ReconcileAsync(cancellationToken);
    }

    [Operation(Position = 51)]
    public async Task LeaveGroupAsync(CancellationToken cancellationToken)
    {
        await _system.GetConnectionForCommand(Uuid).LeaveGroupAsync(cancellationToken);
        await _system.ReconcileAsync(cancellationToken);
    }

    private void EnsureHomeTheater()
    {
        if (!IsHomeTheater)
        {
            throw new InvalidOperationException($"{Title} is not a home theater player.");
        }
    }

    private async Task RunOnCoordinatorAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        await command(_system.GetConnectionForCommand(GroupCoordinatorUuid ?? Uuid), cancellationToken);
        await _system.RefreshAfterCommandAsync(this, cancellationToken);
    }

    private async Task RunOnPlayerAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        await command(_system.GetConnectionForCommand(Uuid), cancellationToken);
        await _system.RefreshAfterCommandAsync(this, cancellationToken);
    }
```

`PlayNotificationAsync` returns `Task<bool>` from the connection; the lambda discards it, which compiles because `Func<..., Task>` accepts `Task<bool>`.

- [ ] **Step 4: Add the group operations**

In `SonosGroup.cs`, change the declaration to:

```csharp
public partial class SonosGroup :
    IAudioPlayer,
    IMediaTrackState,
    IVirtualSubject,
    ITitleProvider,
    IIconProvider
```

Add the using:

```csharp
using Namotion.Devices.Sonos.Client;
```

Add before `Update` (the `*_IsEnabled` properties already exist from Task 7):

```csharp
    [Operation(Position = 1)]
    public Task PlayAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.PlayAsync(token), cancellationToken);

    [Operation(Position = 2)]
    public Task PauseAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.PauseAsync(token), cancellationToken);

    [Operation(Position = 3)]
    public Task StopAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.StopAsync(token), cancellationToken);

    [Operation(Position = 4)]
    public Task NextAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.NextAsync(token), cancellationToken);

    [Operation(Position = 5)]
    public Task PreviousAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.PreviousAsync(token), cancellationToken);

    [Operation(Position = 6)]
    public Task TogglePlaybackAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.TogglePlaybackAsync(token), cancellationToken);

    [Operation(Position = 7)]
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return RunAsync((connection, token) => connection.SeekAsync(position, token), cancellationToken);
    }

    [Operation(Position = 10)]
    public Task SetVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.SetGroupVolumeAsync(SonosValues.ToSonosVolume(volume), token), cancellationToken);

    [Operation(Position = 11)]
    public Task ChangeVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal delta, CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.ChangeGroupVolumeAsync(SonosValues.ToSonosVolumeAdjustment(delta), token), cancellationToken);

    [Operation(Position = 12)]
    public Task MuteAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.SetGroupMuteAsync(true, token), cancellationToken);

    [Operation(Position = 13)]
    public Task UnmuteAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.SetGroupMuteAsync(false, token), cancellationToken);

    private async Task RunAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        await command(_system.GetConnectionForCommand(Coordinator.Uuid), cancellationToken);
        await _system.RefreshAfterCommandAsync(Coordinator, cancellationToken);
    }
```

- [ ] **Step 5: Add the system operations**

In `SonosSystem.cs`, add the using `using Namotion.Interceptor.Registry.Attributes;` and add before `ApplyTopology`:

```csharp
    [Derived]
    [PropertyAttribute("Refresh", KnownAttributes.IsEnabled)]
    public bool Refresh_IsEnabled => IsConnected;

    [Derived]
    [PropertyAttribute("GroupAll", KnownAttributes.IsEnabled)]
    public bool GroupAll_IsEnabled => IsConnected;

    [Derived]
    [PropertyAttribute("UngroupAll", KnownAttributes.IsEnabled)]
    public bool UngroupAll_IsEnabled => IsConnected;

    /// <summary>
    /// Reads topology, state and favorites now instead of at the next poll.
    /// </summary>
    [Operation(Position = 1)]
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The Sonos system is not connected. " + (StatusMessage ?? string.Empty));
        }

        return ReconcileAsync(cancellationToken);
    }

    /// <summary>
    /// Groups every connected room with the given room (party mode).
    /// </summary>
    [Operation(Position = 2)]
    public async Task GroupAllAsync(string coordinatorRoom, CancellationToken cancellationToken)
    {
        var coordinator = FindPlayer(coordinatorRoom) ?? throw CreateUnknownRoomException(coordinatorRoom, nameof(coordinatorRoom));

        // Only a coordinator can be joined, so a grouped target first becomes standalone.
        if (!coordinator.IsGroupCoordinator)
        {
            await GetConnectionForCommand(coordinator.Uuid).LeaveGroupAsync(cancellationToken);
        }

        foreach (var player in Players.Values)
        {
            if (player.IsConnected && !ReferenceEquals(player, coordinator) && player.GroupCoordinatorUuid != coordinator.Uuid)
            {
                await GetConnectionForCommand(player.Uuid).JoinAsync(coordinator.Uuid, cancellationToken);
            }
        }

        await ReconcileAsync(cancellationToken);
    }

    /// <summary>
    /// Makes every room standalone.
    /// </summary>
    [Operation(Position = 3)]
    public async Task UngroupAllAsync(CancellationToken cancellationToken)
    {
        foreach (var player in Players.Values)
        {
            if (player.IsConnected && !player.IsGroupCoordinator)
            {
                await GetConnectionForCommand(player.Uuid).LeaveGroupAsync(cancellationToken);
            }
        }

        await ReconcileAsync(cancellationToken);
    }
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Sonos.Tests`
Expected: all pass.

- [ ] **Step 7: Build the solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Sonos src/HomeBlaze/Namotion.Devices.Sonos.Tests
git commit -m "feat: add Sonos playback, volume, favorites, sound and grouping operations

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Blazor components

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze/Namotion.Devices.Sonos.HomeBlaze.csproj`, `_Imports.razor`, `SonosSystemWidget.razor`, `SonosPlayerWidget.razor`, `SonosGroupWidget.razor`, `SonosSystemEditComponent.razor`
- Modify: `src/Namotion.Interceptor.slnx`

**Interfaces:**
- Consumes: public surface of `SonosSystem`, `SonosPlayer`, `SonosGroup`.
- Produces: `SonosSystemWidget`, `SonosPlayerWidget`, `SonosGroupWidget`, `SonosSystemEditComponent` (namespace `Namotion.Devices.Sonos.HomeBlaze`).

- [ ] **Step 1: Create the project files**

`src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze/Namotion.Devices.Sonos.HomeBlaze.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <ItemGroup>
    <PackageReference Include="MudBlazor" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Devices.Sonos\Namotion.Devices.Sonos.csproj" />
    <ProjectReference Include="..\HomeBlaze.Components.Abstractions\HomeBlaze.Components.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

`src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using MudBlazor
@using Namotion.Interceptor
@using Namotion.Devices.Sonos
@using global::HomeBlaze.Components.Abstractions
@using global::HomeBlaze.Components.Abstractions.Attributes
```

In `src/Namotion.Interceptor.slnx`, after the `Namotion.Devices.Sonos.Tests` line add:

```xml
    <Project Path="HomeBlaze/Namotion.Devices.Sonos.HomeBlaze/Namotion.Devices.Sonos.HomeBlaze.csproj" />
```

- [ ] **Step 2: Create `SonosSystemWidget.razor`**

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(SonosSystem))]
@implements ISubjectComponent

<MudPaper Class="pa-4">
    <MudStack Spacing="2">
        <MudStack Row="true" AlignItems="AlignItems.Center">
            <MudIcon Icon="@Icons.Material.Filled.LibraryMusic" Class="mr-2" />
            <MudText Typo="Typo.h6">Sonos</MudText>
            <MudSpacer />
            <MudChip T="string" Size="Size.Small" Variant="Variant.Filled"
                     Color="@(System?.IsConnected == true ? Color.Success : Color.Error)">
                @(System?.IsConnected == true ? "Connected" : "Disconnected")
            </MudChip>
            <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined"
                     Color="@(System?.AreEventsActive == true ? Color.Success : Color.Warning)">
                @(System?.AreEventsActive == true ? "Events: live" : "Events: polling only")
            </MudChip>
        </MudStack>

        <MudDivider />

        <MudStack Row="true" Spacing="4">
            <MudStack>
                <MudText Typo="Typo.caption">Rooms</MudText>
                <MudText Typo="Typo.h5">@(System?.Players.Count ?? 0)</MudText>
            </MudStack>
            <MudStack>
                <MudText Typo="Typo.caption">Groups</MudText>
                <MudText Typo="Typo.h5">@(System?.Groups.Count ?? 0)</MudText>
            </MudStack>
        </MudStack>

        @if (System is not null)
        {
            @foreach (var player in System.Players.Values)
            {
                <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                    <MudIcon Size="Size.Small"
                             Icon="@(player.IsPlaying == true ? Icons.Material.Filled.PlayCircle : Icons.Material.Filled.Speaker)"
                             Color="@(player.IsConnected ? Color.Default : Color.Error)" />
                    <MudText Typo="Typo.body2">@player.RoomName</MudText>
                    @if (player.IsPlaying == true && player.CurrentTrackTitle is not null)
                    {
                        <MudText Typo="Typo.caption" Class="mud-text-secondary">@player.CurrentTrackTitle</MudText>
                    }
                </MudStack>
            }
        }
    </MudStack>
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private SonosSystem? System => Subject as SonosSystem;
}
```

- [ ] **Step 3: Create `SonosPlayerWidget.razor`**

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(SonosPlayer))]
@implements ISubjectComponent
@inject ISnackbar Snackbar

<MudPaper Class="pa-4">
    <MudStack Spacing="2">
        <MudStack Row="true" AlignItems="AlignItems.Center">
            <MudIcon Icon="@Icons.Material.Filled.Speaker" Class="mr-2" />
            <MudStack Spacing="0">
                <MudText Typo="Typo.h6">@Player?.RoomName</MudText>
                <MudText Typo="Typo.caption">@Player?.Model</MudText>
            </MudStack>
            <MudSpacer />
            @if (Player?.Source is { } source && source != SonosSource.None)
            {
                <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined">@source</MudChip>
            }
            @if (Player?.BatteryLevel is { } battery)
            {
                <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined"
                         Icon="@(Player.IsCharging == true ? Icons.Material.Filled.BatteryChargingFull : Icons.Material.Filled.BatteryStd)">
                    @battery.ToString("P0")
                </MudChip>
            }
        </MudStack>

        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="3">
            @if (Player?.CurrentTrackImageUri is { } image)
            {
                <MudImage Src="@image" Width="64" Height="64" ObjectFit="ObjectFit.Cover" Class="rounded" />
            }
            <MudStack Spacing="0">
                <MudText Typo="Typo.body1">@(Player?.CurrentTrackTitle ?? "Nothing playing")</MudText>
                <MudText Typo="Typo.caption">@Player?.CurrentTrackArtist</MudText>
            </MudStack>
        </MudStack>

        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
            <MudIconButton Icon="@Icons.Material.Filled.SkipPrevious" Disabled="@(Player?.Previous_IsEnabled != true)"
                           OnClick="@(() => RunAsync(player => player.PreviousAsync(CancellationToken.None)))" />
            <MudIconButton Icon="@(Player?.IsPlaying == true ? Icons.Material.Filled.Pause : Icons.Material.Filled.PlayArrow)"
                           Disabled="@(Player?.TogglePlayback_IsEnabled != true)"
                           OnClick="@(() => RunAsync(player => player.TogglePlaybackAsync(CancellationToken.None)))" />
            <MudIconButton Icon="@Icons.Material.Filled.SkipNext" Disabled="@(Player?.Next_IsEnabled != true)"
                           OnClick="@(() => RunAsync(player => player.NextAsync(CancellationToken.None)))" />
            <MudIconButton Icon="@(Player?.IsMuted == true ? Icons.Material.Filled.VolumeOff : Icons.Material.Filled.VolumeUp)"
                           Disabled="@(Player?.Play_IsEnabled != true)"
                           OnClick="@(() => RunAsync(player => player.IsMuted == true ? player.UnmuteAsync(CancellationToken.None) : player.MuteAsync(CancellationToken.None)))" />
            <MudSlider T="int" Min="0" Max="100" Immediate="false" Class="flex-grow-1"
                       Disabled="@(Player?.Play_IsEnabled != true)"
                       Value="@((int)((Player?.Volume ?? 0m) * 100m))"
                       ValueChanged="@(value => RunAsync(player => player.SetVolumeAsync(value / 100m, CancellationToken.None)))" />
        </MudStack>
    </MudStack>
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private SonosPlayer? Player => Subject as SonosPlayer;

    private async Task RunAsync(Func<SonosPlayer, Task> command)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            await command(Player);
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Sonos: {exception.Message}", Severity.Error);
        }
    }
}
```

`Immediate="false"` makes `MudSlider` commit on release. If MudBlazor 9.2 names the parameter differently, check `MudSlider` in the MudBlazor docs and use the "update on release" option it offers; the requirement is one `SetVolumeAsync` per drag.

- [ ] **Step 4: Create `SonosGroupWidget.razor`**

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(SonosGroup))]
@implements ISubjectComponent
@inject ISnackbar Snackbar

<MudPaper Class="pa-4">
    <MudStack Spacing="2">
        <MudStack Row="true" AlignItems="AlignItems.Center">
            <MudIcon Icon="@Icons.Material.Filled.SpeakerGroup" Class="mr-2" />
            <MudText Typo="Typo.h6">@Group?.Title</MudText>
        </MudStack>

        <MudStack Row="true" Spacing="1">
            @foreach (var member in Group?.Members ?? [])
            {
                <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined">@member.RoomName</MudChip>
            }
        </MudStack>

        <MudStack Spacing="0">
            <MudText Typo="Typo.body1">@(Group?.CurrentTrackTitle ?? "Nothing playing")</MudText>
            <MudText Typo="Typo.caption">@Group?.CurrentTrackArtist</MudText>
        </MudStack>

        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
            <MudIconButton Icon="@(Group?.IsPlaying == true ? Icons.Material.Filled.Pause : Icons.Material.Filled.PlayArrow)"
                           Disabled="@(Group?.Play_IsEnabled != true)"
                           OnClick="@(() => RunAsync(group => group.TogglePlaybackAsync(CancellationToken.None)))" />
            <MudSlider T="int" Min="0" Max="100" Immediate="false" Class="flex-grow-1"
                       Disabled="@(Group?.Play_IsEnabled != true)"
                       Value="@((int)((Group?.Volume ?? 0m) * 100m))"
                       ValueChanged="@(value => RunAsync(group => group.SetVolumeAsync(value / 100m, CancellationToken.None)))" />
        </MudStack>
    </MudStack>
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private SonosGroup? Group => Subject as SonosGroup;

    private async Task RunAsync(Func<SonosGroup, Task> command)
    {
        if (Group is null)
        {
            return;
        }

        try
        {
            await command(Group);
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Sonos: {exception.Message}", Severity.Error);
        }
    }
}
```

- [ ] **Step 5: Create `SonosSystemEditComponent.razor`**

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(SonosSystem))]
@implements ISubjectEditComponent

<MudForm>
    <MudTextField @bind-Value="_seedHost"
                  Label="Seed host (any speaker IP, optional)"
                  HelperText="Empty uses known speakers, then SSDP discovery."
                  Immediate="true"
                  OnKeyUp="OnFieldChanged" />

    <MudTextField @bind-Value="_eventCallbackHost"
                  Label="Event callback host (optional)"
                  Placeholder="@(System?.ActiveEventCallbackHost ?? "detected automatically")"
                  HelperText="The address speakers send events to. Set it under Docker bridge networking."
                  Immediate="true"
                  OnKeyUp="OnFieldChanged"
                  Class="mt-4" />

    <MudNumericField @bind-Value="_eventPort" @bind-Value:after="OnFieldChanged"
                     Label="Event port" Min="1" Max="65535" Class="mt-4" />

    <MudNumericField @bind-Value="_pollingIntervalSeconds" @bind-Value:after="OnFieldChanged"
                     Label="Polling interval (seconds)" Min="5" Max="3600" Class="mt-4" />

    <MudNumericField @bind-Value="_retryIntervalSeconds" @bind-Value:after="OnFieldChanged"
                     Label="Retry interval (seconds)" Min="5" Max="3600" Class="mt-4" />

    @if (!IsCreating && System is not null)
    {
        <MudDivider Class="my-4" />
        <MudText Typo="Typo.subtitle2" Class="mb-2">Current State</MudText>
        <MudStack Row="true" Spacing="2" AlignItems="AlignItems.Center">
            <MudChip T="string" Size="Size.Small" Variant="Variant.Filled"
                     Color="@(System.IsConnected ? Color.Success : Color.Error)">
                @(System.IsConnected ? "Connected" : "Disconnected")
            </MudChip>
            <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined"
                     Color="@(System.AreEventsActive ? Color.Success : Color.Warning)">
                @(System.AreEventsActive ? "Events: live" : "Events: polling only")
            </MudChip>
        </MudStack>
        <MudText Class="mt-1">Rooms: @System.Players.Count</MudText>
        @if (System.StatusMessage is not null)
        {
            <MudText Color="Color.Error">@System.StatusMessage</MudText>
        }
    }
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private SonosSystem? System => Subject as SonosSystem;

    private string? _seedHost;
    private string? _eventCallbackHost;
    private int _eventPort;
    private int _pollingIntervalSeconds;
    private int _retryIntervalSeconds;

    private string? _originalSeedHost;
    private string? _originalEventCallbackHost;
    private int _originalEventPort;
    private int _originalPollingIntervalSeconds;
    private int _originalRetryIntervalSeconds;

    public bool IsValid => _eventPort is > 0 and <= 65535 && _pollingIntervalSeconds > 0 && _retryIntervalSeconds > 0;

    public bool IsDirty =>
        _seedHost != _originalSeedHost ||
        _eventCallbackHost != _originalEventCallbackHost ||
        _eventPort != _originalEventPort ||
        _pollingIntervalSeconds != _originalPollingIntervalSeconds ||
        _retryIntervalSeconds != _originalRetryIntervalSeconds;

    public event Action<bool>? IsValidChanged;

    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        _seedHost = System?.SeedHost;
        _eventCallbackHost = System?.EventCallbackHost;
        _eventPort = System?.EventPort ?? 6329;
        _pollingIntervalSeconds = (int)(System?.PollingInterval.TotalSeconds ?? 30);
        _retryIntervalSeconds = (int)(System?.RetryInterval.TotalSeconds ?? 30);
        StoreOriginals();
    }

    private void OnFieldChanged()
    {
        IsValidChanged?.Invoke(IsValid);
        IsDirtyChanged?.Invoke(IsDirty);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (System is not null && IsValid)
        {
            System.SeedHost = string.IsNullOrWhiteSpace(_seedHost) ? null : _seedHost.Trim();
            System.EventCallbackHost = string.IsNullOrWhiteSpace(_eventCallbackHost) ? null : _eventCallbackHost.Trim();
            System.EventPort = _eventPort;
            System.PollingInterval = TimeSpan.FromSeconds(_pollingIntervalSeconds);
            System.RetryInterval = TimeSpan.FromSeconds(_retryIntervalSeconds);
            StoreOriginals();
            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }

    private void StoreOriginals()
    {
        _originalSeedHost = _seedHost;
        _originalEventCallbackHost = _eventCallbackHost;
        _originalEventPort = _eventPort;
        _originalPollingIntervalSeconds = _pollingIntervalSeconds;
        _originalRetryIntervalSeconds = _retryIntervalSeconds;
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.slnx src/HomeBlaze/Namotion.Devices.Sonos.HomeBlaze
git commit -m "feat: add Sonos widgets and the system edit form

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: HomeBlaze registration, sample configuration and documentation

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, `src/HomeBlaze/HomeBlaze/Program.cs`
- Create: `src/HomeBlaze/HomeBlaze/Data/Files/Devices/Sonos.json`, `src/HomeBlaze/HomeBlaze/Data/Files/Docs/devices/Sonos.md`

- [ ] **Step 1: Reference the projects**

In `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, after the two `Namotion.Devices.Shelly` project references add:

```xml
        <ProjectReference Include="..\Namotion.Devices.Sonos\Namotion.Devices.Sonos.csproj" />
        <ProjectReference Include="..\Namotion.Devices.Sonos.HomeBlaze\Namotion.Devices.Sonos.HomeBlaze.csproj" />
```

- [ ] **Step 2: Register the assemblies**

In `src/HomeBlaze/HomeBlaze/Program.cs`, add after `using Namotion.Devices.Shelly.HomeBlaze;`:

```csharp
using Namotion.Devices.Sonos;
using Namotion.Devices.Sonos.HomeBlaze;
```

Replace the last two `AddAssembly` lines of the `typeProvider` chain:

```csharp
    .AddAssembly(typeof(LuxtronikHeatPump).Assembly)                                        // Namotion.Devices.Luxtronik
    .AddAssembly(typeof(LuxtronikHeatPumpWidget).Assembly);                                 // Namotion.Devices.Luxtronik.HomeBlaze
```

with:

```csharp
    .AddAssembly(typeof(LuxtronikHeatPump).Assembly)                                        // Namotion.Devices.Luxtronik
    .AddAssembly(typeof(LuxtronikHeatPumpWidget).Assembly)                                  // Namotion.Devices.Luxtronik.HomeBlaze
    .AddAssembly(typeof(SonosSystem).Assembly)                                              // Namotion.Devices.Sonos
    .AddAssembly(typeof(SonosSystemWidget).Assembly);                                       // Namotion.Devices.Sonos.HomeBlaze
```

- [ ] **Step 3: Add the sample configuration**

`src/HomeBlaze/HomeBlaze/Data/Files/Devices/Sonos.json`:

```json
{
  "$type": "Namotion.Devices.Sonos.SonosSystem",
  "seedHost": null,
  "eventCallbackHost": null,
  "eventPort": 6329,
  "pollingInterval": "00:00:30",
  "retryInterval": "00:00:30"
}
```

- [ ] **Step 4: Write the device documentation**

`src/HomeBlaze/HomeBlaze/Data/Files/Docs/devices/Sonos.md`:

````markdown
---
title: Sonos
icon: LibraryMusic
---

# Sonos

One `SonosSystem` represents a Sonos household. It finds the speakers itself, shows each room as a player with its bonded surrounds and subwoofer, and shows the groups the rooms currently form. State arrives through UPnP events as it changes, and a poll every 30 seconds reconciles everything the events might have missed.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SeedHost` | string | null | Any speaker of the household as `host` or `host:port`. Empty uses the speakers known from the last run, then SSDP discovery. |
| `EventCallbackHost` | string | null | The address speakers send events to. Empty detects the local address that routes to the seed speaker. |
| `EventPort` | int | 6329 | The port of the event listener. |
| `PollingInterval` | TimeSpan | 30 seconds | How often topology, state and favorites are reconciled. |
| `RetryInterval` | TimeSpan | 30 seconds | Delay before reconnecting after a failure. |

### Discovery

The system needs one reachable speaker. It reads the household topology from that speaker, which lists every room, satellite and group with their addresses. With no `SeedHost` it searches the network over SSDP multicast, which does not cross Docker bridge networks. Set `SeedHost` to any speaker IP in that case.

### Events

Speakers push changes to `http://<EventCallbackHost>:<EventPort>/event/...`. The listener runs inside the HomeBlaze process on its own port; no second web server is involved.

- Under Docker host networking the detected address works. Under bridge networking it is the container address, which speakers cannot reach: set `EventCallbackHost` to the host IP and publish `EventPort`.
- Open `EventPort` in the host firewall.
- If the port cannot be bound or no subscription succeeds, the system keeps working on polling alone and shows "Events: polling only". Spotify Connect track details are only delivered through events, so they stay empty in that mode.

## Subjects

```
SonosSystem
├── Players[<uuid>]         SonosPlayer, one per room
│     └── Satellites[<uuid>]  SonosSatellite: surround, subwoofer or stereo partner
└── Groups[<coordinator uuid>]  SonosGroup, the rooms playing together
```

Keys are the RINCON ids of the speakers, so paths stay valid across restarts and IP changes. A group is keyed by its coordinator; a room that is not grouped is a group of one, as in the Sonos app. A speaker that disappears from the topology stays in place and reports `IsConnected = false` until restart.

## Operations

### SonosPlayer

| Operation | Description |
|-----------|-------------|
| `Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback` | Playback, sent to the group coordinator |
| `Seek` | Jumps within the current track (only when it has a duration) |
| `SetVolume`, `ChangeVolume`, `RampVolume` | Volume 0 to 1, relative change, or a smooth ramp |
| `Mute`, `Unmute` | Mute of this room |
| `PlayFavorite` | Plays a Sonos favorite by name, see `SonosSystem.Favorites` |
| `PlayUri` | Plays an http(s) stream as radio (with an optional title) or a native Sonos URI |
| `PlayNotification` | Plays a sound over the current playback, which then resumes (S2 speakers) |
| `SwitchToTv`, `SwitchToLineIn` | Selects the TV or line-in input where available |
| `SetShuffle`, `SetRepeat`, `SetSleepTimer` | Play mode and sleep timer (zero cancels) |
| `SetBass`, `SetTreble`, `SetLoudness` | Sound settings, bass and treble from -10 to 10 |
| `SetNightMode`, `SetSpeechEnhancement` | Home theater sound settings |
| `JoinGroup`, `LeaveGroup` | Joins another room's group by room name, or becomes standalone |

### SonosGroup

`Play`, `Pause`, `Stop`, `Next`, `Previous`, `TogglePlayback`, `Seek`, and group-wide `SetVolume`, `ChangeVolume`, `Mute`, `Unmute`. Group volume keeps the volume ratio between the rooms.

### SonosSystem

| Operation | Description |
|-----------|-------------|
| `Refresh` | Reconciles now instead of at the next poll |
| `GroupAll` | Groups every room with the given room |
| `UngroupAll` | Makes every room standalone |

## State Properties

### SonosPlayer

| Property | Description |
|----------|-------------|
| `TransportState`, `IsPlaying`, `Source` | Playback state and where the audio comes from (TV, line-in, Spotify Connect, AirPlay, radio, queue) |
| `Volume`, `IsMuted` | Volume 0 to 1 and mute |
| `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `CurrentTrackImageUri`, `CurrentTrackUri`, `CurrentTrackPosition`, `CurrentTrackDuration` | The current track |
| `Shuffle`, `Repeat`, `SleepTimerRemaining` | Play mode and sleep timer |
| `Bass`, `Treble`, `Loudness`, `NightMode`, `SpeechEnhancement` | Sound settings; night mode and speech enhancement only on home theater players |
| `GroupCoordinatorUuid`, `IsGroupCoordinator` | Group membership |
| `BatteryLevel`, `IsCharging` | Battery of portable speakers |
| `Model`, `ProductCode`, `SerialNumber`, `HardwareRevision`, `SoftwareVersion`, `IpAddress`, `MacAddress`, `IsWireless`, `IsConnected` | Identity, network and connection |

### SonosSystem

`Players`, `Groups`, `Favorites`, `AreEventsActive`, `ActiveEventCallbackHost`, `IsConnected`, `Status`, `StatusMessage`, `LastUpdated`.

## Limitations

- `Sonos.Base`, used for the SOAP calls, parses responses with `XmlSerializer`, so this library is not yet free of runtime reflection for Native AOT. Our own parsers use LINQ to XML.
- Album art is served by the speaker over plain http. When HomeBlaze itself is served over https, browsers block it as mixed content.
- On Windows, listening on all interfaces with `HttpListener` needs a URL ACL or administrator rights. Linux, macOS and Docker need nothing.
- Notification clips use the Sonos audio clip API, which needs S2 speakers.
- Favorites of type "shortcut" (for example Sonos Radio station shortcuts) carry no URI and are not listed; see the follow-ups.

## Follow-ups

Not implemented yet; each fits the current structure:

- Alarms: list Sonos alarms as state, with enable and disable operations (`AlarmClockService`).
- Shortcut favorites through the Sonos local websocket favorites API.
- Queue editing, Sonos playlists and music library browsing beyond favorites.
- An album art proxy so artwork loads when HomeBlaze is served over https.
- Windows support for the event listener without a URL ACL, for example a raw socket listener.
- An AOT-clean SOAP client replacing `Sonos.Base`.
- `AvailableSoftwareUpdate` via `CheckForUpdate`.
- LED state and button lock settings.
- Home theater TV power state and other HTControl details.
- Opt-in live integration tests (`Category=Integration`, read-only, speaker IP from an environment variable).
- Upstream contributions to `Sonos.Base` (satellites in the topology model, `resMD` in DIDL) so the library's own parsers can shrink.
````

- [ ] **Step 5: Build and run the unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze
git commit -m "feat: register the Sonos library in HomeBlaze and document it

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Verification against the live household

No code changes unless a check fails. Read-only first.

- [ ] **Step 1: Full verification**

Run: `dotnet build src/Namotion.Interceptor.slnx` then `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 2: Run HomeBlaze against the real speakers (read-only)**

Use the `run` skill (or the project's documented launch) to start HomeBlaze with `Sonos.json` set to `"seedHost": "192.168.1.101"`. Confirm in the UI or through the HomeBlaze MCP tools:
- 4 players (Wohnzimmer, Terasse, Küche, Büro), 6 satellites with roles, 4 groups.
- Arc and Ray report `IsHomeTheater = true`, the Roam reports a battery level, all report software version 18.8.
- `Favorites` lists "Radio FM1" and "SRF 3".
- `AreEventsActive` is true, and changing the volume in the Sonos app updates `Volume` within a second.
- Starting Spotify Connect from a phone shows the track title and artist.

Record any NOTIFY body that parses unexpectedly (enable debug logging for `Namotion.Devices.Sonos`), add it as a fixture test, fix, and commit with `fix:`.

- [ ] **Step 3: Ask before write operations**

Ask the user for permission, naming the room and operation, before each of: volume ±1 on an idle room, `PlayFavorite("SRF 3")` then `Pause`, `PlayNotification`, `JoinGroup`/`LeaveGroup`. Run only what the user approves and report the outcome.

- [ ] **Step 4: Review the v1 checklist**

Walk the "v1 feature checklist" table in the spec and confirm each row against the running system. Report anything missing.

---

## Self-review notes

- Spec coverage: abstractions (Task 1), projects and packages (2), own parsers (3 to 5), event listener (6), subject hierarchy, offline handling, groups keyed by coordinator, event versus poll ordering, `NOT_IMPLEMENTED` handling (7), Sonos.Base client and discovery (8), main loop, seed selection, failure tolerance, subscriptions, teardown, DI extension (9), all operations and `IsEnabled` (10), UI (11), HomeBlaze wiring, sample JSON, docs with follow-ups (12), live verification and v1 checklist (13).
- Additions beyond the spec, both small: `ActiveEventCallbackHost` state (feeds the edit form placeholder the spec asked for) and skipping shortcut favorites (found in live data; documented as a follow-up).
