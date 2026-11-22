namespace Namotion.Devices.Tesla;

/// <summary>Whether a vehicle is reachable and awake.</summary>
public enum TeslaVehicleState { Unknown, Online, Asleep, Offline }

/// <summary>Whether the vehicle requires signed commands.</summary>
public enum TeslaCommandSigning { Unknown, Off, Allowed, Required }

/// <summary>The charging state that the vehicle reports.</summary>
public enum TeslaChargingState { Unknown, Disconnected, NoPower, Starting, Charging, Complete, Stopped, Calibrating }

/// <summary>The gear of the vehicle.</summary>
public enum TeslaShiftState { Unknown, Park, Reverse, Neutral, Drive }

/// <summary>When scheduled charging starts.</summary>
public enum TeslaScheduledChargingMode { Unknown, Off, StartAt, DepartBy }

/// <summary>The state of the charge port latch.</summary>
public enum TeslaChargePortLatch { Unknown, Engaged, Disengaged, Blocking }

/// <summary>The mode of the cabin overheat protection.</summary>
public enum TeslaCabinOverheatProtection { Unknown, Off, On, FanOnly }

/// <summary>The temperature at which the cabin overheat protection starts.</summary>
public enum TeslaCabinOverheatProtectionTemperature { Unknown, Low, Medium, High }

/// <summary>The climate keeper mode.</summary>
public enum TeslaClimateKeeperMode { Unknown, Off, On, Dog, Camp }

/// <summary>Whether the automatic climate control follows its setting or was overridden.</summary>
public enum TeslaHvacAutoRequest { Unknown, On, Override }

/// <summary>The playback status of the media player.</summary>
public enum TeslaMediaPlaybackStatus { Unknown, Stopped, Playing, Paused }

/// <summary>The days on which a schedule applies.</summary>
public enum TeslaScheduleDays { Unknown, AllWeek, Weekdays }

/// <summary>The progress of a software update.</summary>
public enum TeslaSoftwareUpdateStatus { Unknown, Available, Scheduled, Installing, Downloading, DownloadingWifiWait }
