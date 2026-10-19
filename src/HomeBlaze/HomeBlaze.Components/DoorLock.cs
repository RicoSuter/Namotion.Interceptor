using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A tile that shows a door lock and its door, and locks or unlocks it with one click.
/// </summary>
[Category("Dashboard")]
[Description("A tile that shows a door lock and its door, and locks or unlocks it")]
[DisplayName("Door lock")]
[SubjectIcon("Lock")]
[InterceptorSubject]
public partial class DoorLock : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Path of the lock, a subject that implements <see cref="IDoorLockState"/>. When it also implements
    /// <see cref="HomeBlaze.Abstractions.Sensors.IDoorSensor"/>, the icon shows the door.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(IDoorLockState))]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label of the tile. Defaults to the title of the lock.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// Name or path of the <see cref="ValueMap"/> that gives a lock state, by its name such as "Locked", the fill, the
    /// badge icon and the text of the tile. A state without a case keeps the fill, badge and text of the tile.
    /// </summary>
    [Configuration]
    [SubjectPath(typeof(ValueMap))]
    public partial string? Map { get; set; }

    /// <summary>
    /// The second line of the tile. Defaults to the text of the matched case of the map, otherwise to the state of the lock.
    /// </summary>
    [Configuration]
    public partial string? Subtitle { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
