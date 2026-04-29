using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Marker interface for non-physical aggregations (rooms, zones, groups).
/// </summary>
[SubjectAbstraction]
[Description("Non-physical virtual subject (e.g., room, zone, or group).")]
public interface IVirtualSubject
{
}
