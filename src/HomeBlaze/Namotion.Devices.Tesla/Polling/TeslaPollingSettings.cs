namespace Namotion.Devices.Tesla.Polling;

/// <summary>The intervals with which the vehicle_data of an awake vehicle is read.</summary>
/// <param name="ActiveInterval">The interval while the vehicle charges, drives or has a user present.</param>
/// <param name="IdleInterval">The interval while the vehicle is not active.</param>
/// <param name="IdleTimeout">How long the vehicle may sleep but stays awake before reading pauses.</param>
/// <param name="SleepWindow">How long reading pauses so that the vehicle can fall asleep.</param>
internal readonly record struct TeslaPollingSettings(
    TimeSpan ActiveInterval,
    TimeSpan IdleInterval,
    TimeSpan IdleTimeout,
    TimeSpan SleepWindow);
