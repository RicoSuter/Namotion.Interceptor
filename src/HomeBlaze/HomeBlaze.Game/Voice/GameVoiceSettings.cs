namespace HomeBlaze.Game.Voice;

/// <summary>
/// Every timing and size of voice in the game, the one place to tune them. The page passes the browser's values to
/// game-voice.js, which holds no copy.
/// </summary>
internal static class GameVoiceSettings
{
    private const int FrameMilliseconds = 20;

    /// <summary>
    /// Samples per second of every frame.
    /// </summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// Samples in one frame of <see cref="FrameDuration"/>.
    /// </summary>
    public const int SamplesPerFrame = SampleRate * FrameMilliseconds / 1000;

    /// <summary>
    /// Bytes in one frame of 16-bit samples.
    /// </summary>
    public const int FrameBytes = SamplesPerFrame * 2;

    /// <summary>
    /// Frames a talker queue needs before it plays.
    /// </summary>
    public const int ServerQueueStart = 1;

    /// <summary>
    /// Frames beyond which a talker queue drops the oldest.
    /// </summary>
    public const int ServerQueueMaximum = 6;

    /// <summary>
    /// Frames a buffer grows by after it ran dry.
    /// </summary>
    public const int BufferGrowth = 1;

    /// <summary>
    /// Sends to a page in flight beyond which new frames to it are dropped.
    /// </summary>
    public const int MaximumPendingSends = 10;

    /// <summary>
    /// Allowed incoming frame rate as a multiple of the nominal rate.
    /// </summary>
    public const double FrameRateTolerance = 1.5;

    /// <summary>
    /// Length of one frame.
    /// </summary>
    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(FrameMilliseconds);

    /// <summary>
    /// Audio the browser buffers before it plays.
    /// </summary>
    public static readonly TimeSpan PlaybackBufferStart = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Largest buffer the browser grows to.
    /// </summary>
    public static readonly TimeSpan PlaybackBufferMaximum = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Time without running dry before a buffer shrinks by one frame.
    /// </summary>
    public static readonly TimeSpan BufferShrinkDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Shortest time between two rebuilds of the volume table.
    /// </summary>
    public static readonly TimeSpan VolumeTableInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Time after which a frame sent to a page counts as lost and no longer as pending.
    /// </summary>
    public static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Time a page waits for the browser to start its microphone, which includes the user answering the permission prompt.
    /// </summary>
    public static readonly TimeSpan CaptureStartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Time a page waits for any other voice call to the browser.
    /// </summary>
    public static readonly TimeSpan BrowserCallTimeout = TimeSpan.FromSeconds(10);
}
