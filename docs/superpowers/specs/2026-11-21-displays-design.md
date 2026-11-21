# Displays: SVG templates on an e-paper display

## Goal

Show live HomeBlaze values on a small display attached to the Raspberry Pi, starting with the Waveshare 2.13" e-Paper HAT (250×122, black and white, SSD1680 controller). The content is a generic, configurable layout: an SVG template with `{{ }}` expressions, edited in HomeBlaze with a live preview of the exact pixels the display will show.

## Non-goals

- HTML or Blazor rendering of existing widget pages.
- Color or three-color panels, grayscale, dithering.
- Controllers other than SSD1680. A second controller is added in its own change when someone has the hardware.
- GPIO providers other than `GpioSubject`, such as I2C expanders.
- Templates stored as separate files. The template lives in the display configuration.

## Projects

| Project | Change |
|---|---|
| `HomeBlaze.Abstractions` | New `ITextTemplateRenderer` |
| `HomeBlaze.Services` | Implements `ITextTemplateRenderer` on the existing expression display code; adds an `ExpressionUse` value for device templates; registered by the host |
| `Namotion.Devices.Gpio` | New public pin lease API on `GpioSubject` |
| `Namotion.Devices.Displays` (new) | `PixelDisplay`, `Ssd1680Display`, `SimulatedPixelDisplay` and their parts. References `HomeBlaze.Abstractions`, `Namotion.Devices.Gpio`, `Namotion.Interceptor.Expressions`, `Namotion.Interceptor.Registry`, `Svg.Skia` (SkiaSharp) |
| `Namotion.Devices.Displays.HomeBlaze` (new) | Edit component (template editor and live preview) and widget, registered on `PixelDisplay` |
| `Namotion.Devices.Displays.Tests` (new) | Unit tests |

Like every `Namotion.Devices.*` library, the display library references only abstractions and works outside HomeBlaze. Template formatting is reached through `ITextTemplateRenderer`, which the display gets from its context with `TryGetService`, as FritzBox gets `ISubjectPathResolver`. When no renderer is registered, the display uses its internal default renderer, see [Template renderer abstraction](#template-renderer-abstraction).

Everything ships in one pull request. The lease API comes first in its own commits.

## Template renderer abstraction

`ITextTemplateRenderer` in `HomeBlaze.Abstractions` parses a text with `{{ }}` expressions once and renders it against a scope subject:

- Parsing returns a parsed template or an error of the form `<message> at position <n>`.
- Rendering takes the scope subject and an encoder that is applied to each evaluated value, never to the literal text around it. The display passes an XML encoder, so a value such as `A & B` cannot break the SVG.
- A template has no variables. How paths resolve is up to the implementation, see below.

There are two implementations:

- **HomeBlaze** (`HomeBlaze.Services`, registered by the host) resolves paths like a widget, relative to the scope subject or absolute with `/`, and formats values as everywhere else in HomeBlaze: units, the pipes `display`, `raw` and `map`, value maps, and masking of secret configuration values.
- **Default** (internal to `Namotion.Devices.Displays`) is used when the context has no `ITextTemplateRenderer`. It parses with `TextTemplate` from `Namotion.Interceptor.Expressions`, resolves paths through the registry from the root of the subject graph that holds the scope subject (the registry has no root of its own, so the root is found by following the first parent), and formats values in the invariant culture with `ToString()`. It knows no pipe, so a template with a pipe is a parse error.

So the host decides how a template looks: the same template can show `11 kW` in HomeBlaze and `11` elsewhere.

## Subjects

Inheritance at the subject level, composition below it. A display is one device in the configuration; the subjects are thin glue for configuration, lifecycle and status, and the work happens in plain classes that are tested on their own.

```
PixelDisplay (abstract subject)      BackgroundService, IConfigurable, IMonitoredService
  configuration  Template (SVG text), RefreshInterval (default 60 s), RetryInterval (default 30 s)
  state          Status, StatusMessage, LastRefreshTime
  abstract       Width, Height (native), Rotation, PixelFormat, ConnectAsync, ShowAsync(frame, cancellationToken), DisconnectAsync
  parts          DisplayTemplate, DisplayRefreshScheduler, MonochromeFrame

Ssd1680Display : PixelDisplay
  configuration  Width 122, Height 250, Rotation 90, Gpio (path to a GpioSubject),
                 ResetPin 17, DataCommandPin 25, BusyPin 24, PowerPin 18 (optional, null to skip),
                 SpiBus 0, SpiChipSelect 0
  parts          Ssd1680Controller (internal): the protocol over SpiDevice and the leased pins

SimulatedPixelDisplay : PixelDisplay
  configuration  Width, Height, Rotation
  behavior       keeps the last frame; used for development without hardware and as the test double of PixelDisplay
```

The pin defaults are the pinout of the Waveshare HAT. The panel is natively portrait, so the default rotation of 90 gives a 250×122 landscape canvas.

Nothing in `PixelDisplay` is specific to e-paper, so that other pixel displays, such as an SSD1306 OLED or an ILI9341 color TFT, can be added later as further subclasses:

- `PixelFormat` is an enum with the single value `Monochrome` for now. `PixelDisplay` chooses the frame conversion from it. A color display later adds an enum value and a frame type next to `MonochromeFrame`.
- `ShowAsync` takes only the frame. Whether an update is a full or a partial refresh is the policy of the e-paper display itself.

Displays without pixels, such as character LCDs, do not fit this model. They would get their own base class in the same library, such as a `CharacterDisplay`, and are out of scope for this change.

### Parts

- **`DisplayTemplate`** parses the SVG text through `ITextTemplateRenderer` when the template changes. It renders the text with XML encoding, loads the SVG with Svg.Skia and draws it on a white `SKBitmap` of the logical size (native size after rotation), scaling the SVG `viewBox` to fit. It embeds one font (DejaVu Sans) as the default typeface, because the Docker image has no system fonts, and turns off the JavaScript support of Svg.Skia.
- **`DisplayRefreshScheduler`** decides when to render, driven by a `TimeProvider`. It has no knowledge of SVG or hardware.
- **`MonochromeFrame`** packs the bitmap to one bit per pixel by a luminance threshold of 50 %, which also takes care of antialiased edges, and rotates it to the native orientation: rows padded to whole bytes, most significant bit first, 1 for white, as the SSD1680 expects. It compares by content for the frame diff.

## Data flow

1. A template change is parsed once. A parse error, or an SVG that fails to load, goes to `StatusMessage`; the display stays `Running` and the panel keeps the last good frame.
2. A render runs inside a `ReadPropertyRecorder` scope: evaluate the template, draw the SVG, convert to a `MonochromeFrame`. The recorded properties become the new watch set, so the set follows paths whose target changes.
3. The display subscribes to property changes and marks itself dirty when a watched property changes. The notification only sets a flag; rendering and SPI traffic happen on the display's background loop.
4. The frame is pushed to the panel only when it differs from the last pushed frame.

### Refresh timing

`RefreshInterval` is the shortest time between two frames pushed to the panel:

- A timer renders every `RefreshInterval`, for values that send no change notification, such as the time of day. A frame that did not change is not pushed.
- A change of a watched property renders right away when `RefreshInterval` has passed since the last push, and otherwise when it elapses. So after a quiet period, in which the timer rendered only unchanged frames, a change shows at once. Several changes within an interval collapse into one render.
- Change tracking needs the read recorder of the context (`WithReadPropertyRecorder`), which HomeBlaze configures. Without it, the timer alone renders.

### SSD1680 refresh policy

- The first frame after a connect is a full refresh.
- Later frames are partial refreshes, with a full refresh after every 20 partial refreshes and at least once an hour, to clear ghosting.
- The analog circuits power down after each update.
- On stop, the panel enters deep sleep and keeps its last image. It is not cleared.

## Pin lease API

```csharp
public GpioPinLease LeasePins(IInterceptorSubject owner, IReadOnlyList<GpioPinLeaseRequest> pins);

public readonly record struct GpioPinLeaseRequest(int PinNumber, GpioPinLeaseMode Mode);

public enum GpioPinLeaseMode { Input, InputPullUp, Output, Reserved }

public sealed class GpioPinLease : IDisposable
{
    public CancellationToken Revoked { get; }
    public void Write(int pinNumber, PinValue value);
    public PinValue Read(int pinNumber);
    public void Dispose();
}
```

- Leased pins stay in `Pins`, because a reload rebuilds `Pins` for every pin number, but they are detached and closed, and the lease opens them in the requested mode. They show `ServiceStatus.Unavailable` with the message `Used by <owner>`, where the name is the owner's `ITitleProvider.Title`, or its type's display name. The message is set at lease time.
- `Reserved` pins are marked but never opened. The display reserves the SPI0 pins (GPIO 8 to 11), because the Raspberry Pi pin controller lets libgpiod take over a pin that the SPI driver muxed, which breaks SPI.
- `LeasePins` throws deterministically when GPIO is not `Running`, for a pin outside the pin count, for a pin an ADC reserves, for a pin that is already leased, and when the owner already holds a lease.
- The owner is kept in a plain field, never in a tracked property, so that it does not become a child of a pin in the subject graph.
- A configuration reload that keeps the controller keeps the leases, and leaves their pins out of the reload. When the controller is released (stop, dispose, or a change of `UseSimulation`), every lease is revoked: `Revoked` is canceled and later calls of `Read` and `Write` throw.
- `Read` and `Write` take `_controllerLock` briefly. SPI traffic never holds it.
- Disposing a lease returns its pins to `GpioSubject`, which opens them again in their configured mode.
- With an injected driver, releasing the controller does not close pins, so revoking a lease closes the pins it opened. The `Revoked` token is canceled with `CancelAsync`, so that no callback runs under the controller lock.

## Lifecycle and errors

| Situation | Behavior |
|---|---|
| Connect | Resolve the GPIO subject by path (through `ISubjectPathResolver` when the context has one, otherwise through the registry from the graph root), wait until it is `Running`, lease the pins, open SPI, initialize the controller, full refresh. |
| Connect fails | `Status` is `Error` with the message; retry after `RetryInterval`. |
| Lease revoked | `Status` is `Unavailable` with `GPIO not running`; reconnect when GPIO runs again. |
| BUSY stays high beyond a timeout | The update fails with an error; the display reconnects, which re-initializes the controller. |
| Template or SVG error | `StatusMessage` shows it; the panel keeps the last good frame; `Status` stays `Running`. |
| Stop or dispose | Deep sleep, release SPI and the lease. |

## Editor and widget

- The edit component, registered on `PixelDisplay`, shows the configuration fields and the template in a multi-line monospace text field next to a preview. Monaco is left out: its safe wrapper lives in HomeBlaze.Storage.Blazor, which a device library does not reference, and it does not work on phones. The preview calls `RenderPreview(templateText)` on the display, debounced by 300 ms, which runs the render steps without recording and without pushing, and returns a PNG of the monochrome frame. The browser scales it up with `image-rendering: pixelated`. A template error is shown below the preview.
- The widget shows the last pushed frame as a PNG, the status, and the time of the last refresh.

## Testing

| Area | Tests |
|---|---|
| Pin lease | Pins marked and restored; each fail-fast case; revocation on stop and on a change of `UseSimulation`; leases survive a reload; works on the simulation driver (`LatchingGpioDriver`) |
| `ITextTemplateRenderer` (HomeBlaze) | Units, pipes, value maps, encoder applied per value only, secrets masked |
| Default renderer | Registry paths, invariant formatting, encoder applied per value only, a pipe is a parse error |
| `DisplayRefreshScheduler` | Change before and after the interval, timer fallback, collapsing of changes (`FakeTimeProvider`) |
| `DisplayTemplate` | XML encoding, template and SVG errors, recorded reads, embedded font renders text |
| `MonochromeFrame` | Threshold, rotation, bit packing, equality |
| `Ssd1680Controller` | Recorded SPI bytes and pin writes for init, full refresh, partial refresh and sleep; BUSY timeout (fake `SpiDevice` and fake pins) |
| `SimulatedPixelDisplay` | A property change leads to a new frame within the interval; an unchanged frame is not pushed; a template error keeps the last frame; status transitions |
| Editor | Component test of the debounced preview, if the Blazor test setup supports it; no E2E test |

Manual verification before merge, on a Raspberry Pi with the HAT: the first full refresh, live partial updates, ghosting after many partial refreshes, and a GPIO configuration reload while the display runs. The SSD1680 sequence is written from the datasheet and the Waveshare reference code, so this is the only check against real hardware.
