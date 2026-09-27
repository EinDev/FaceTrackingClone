namespace FaceTrackingClone.Vive;

/// <summary>
/// One decoded luminance frame from the facial tracker.
///
/// The device delivers a 400x400 YUY2 frame that is actually a *stereo pair*: two 200x400
/// views of the lower face side by side. Only the left view is used, matching the reference
/// implementation, and it is stretched back to a square for the inference model.
/// </summary>
internal sealed class LipFrame
{
    public const int NativeWidth = 400;
    public const int NativeHeight = 400;
    public const int EyeWidth = 200;

    /// <summary>Full-frame luminance, NativeWidth * NativeHeight, one byte per pixel.</summary>
    public byte[] Luma { get; } = new byte[NativeWidth * NativeHeight];

    /// <summary>Monotonic capture timestamp in milliseconds since driver start.</summary>
    public double TimestampMs { get; set; }

    /// <summary>Sequence number, so consumers can detect skipped frames.</summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Copies the left half (the usable view) into <paramref name="dest"/>, rescaled to
    /// <paramref name="size"/> square with nearest-neighbour sampling.
    /// </summary>
    public void CopyLeftViewScaled(byte[] dest, int size)
    {
        if (dest.Length < size * size)
            throw new ArgumentException("Destination too small.", nameof(dest));

        for (int y = 0; y < size; y++)
        {
            int srcY = y * NativeHeight / size;
            int srcRow = srcY * NativeWidth;
            int dstRow = y * size;
            for (int x = 0; x < size; x++)
            {
                int srcX = x * EyeWidth / size;
                dest[dstRow + x] = Luma[srcRow + srcX];
            }
        }
    }

    public void CopyFrom(LipFrame other)
    {
        Array.Copy(other.Luma, Luma, Luma.Length);
        TimestampMs = other.TimestampMs;
        Sequence = other.Sequence;
    }
}

/// <summary>Health of the capture pipeline, polled by consumers and by the watchdog.</summary>
internal enum TrackerState
{
    Stopped,
    Starting,
    Streaming,
    /// <summary>Frames have stopped arriving; recovery is in progress.</summary>
    Stalled,
    /// <summary>Device is gone entirely (unplugged); waiting for it to come back.</summary>
    Disconnected,
    Faulted
}

internal sealed record TrackerHealth(
    TrackerState State,
    long FramesReceived,
    long Recoveries,
    double Fps,
    double MsSinceLastFrame,
    string? LastError)
{
    public override string ToString() =>
        $"{State,-12} frames={FramesReceived,-8} fps={Fps,5:0.0} " +
        $"age={MsSinceLastFrame,6:0}ms recoveries={Recoveries}" +
        (LastError is null ? "" : $"  last error: {LastError}");
}
