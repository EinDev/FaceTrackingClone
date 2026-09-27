using System.IO.MemoryMappedFiles;
using FaceTrackingClone.Vive;

namespace FaceTrackingClone.Ipc;

/// <summary>
/// Shared-memory transport for the live camera feed and pipeline stats.
///
/// A UVC device can only be opened by one process at a time, so a viewer cannot independently
/// open the tracker while the driver (or the VRCFT module) owns it. Instead the owner publishes
/// each frame here and any number of viewers read it.
///
/// Synchronisation is a seqlock: the writer bumps the sequence to an odd value, writes, then
/// bumps it to even. A reader samples the sequence before and after copying and retries if it
/// changed. That keeps the 60Hz capture path lock-free -- a viewer can never stall the driver,
/// which matters because the driver is the thing that must never hang.
/// </summary>
internal static class FrameShare
{
    /// <summary>
    /// Stable across versions on purpose. Versioning the *name* makes a mismatched publisher and
    /// subscriber simply fail to find each other, which looks identical to "nothing is running".
    /// Keeping the name fixed and versioning <see cref="Magic"/> instead turns that into a
    /// diagnosable error.
    /// </summary>
    public const string DefaultName = @"Local\ftclone.lipframe";

    private const int Magic = 0x4654_4C32; // "FTL2" - bump when the layout changes

    /// <summary>Blendshape weights carried alongside each frame, so viewers can render them.</summary>
    public const int WeightCount = 45;

    // Header layout (byte offsets).
    private const int OffMagic = 0;
    private const int OffWidth = 4;
    private const int OffHeight = 8;
    private const int OffState = 12;
    private const int OffSequence = 16;   // long
    private const int OffTimestamp = 24;  // double
    private const int OffFps = 32;        // double
    private const int OffFrames = 40;     // long
    private const int OffRecoveries = 48; // long
    private const int OffPublisherPid = 56;
    private const int OffHasWeights = 60;
    private const int HeaderSize = 64;

    private const int OffWeights = HeaderSize;              // 45 floats
    private const int WeightBytes = WeightCount * sizeof(float);
    private const int OffPixels = OffWeights + WeightBytes;

    private const int PixelCount = LipFrame.NativeWidth * LipFrame.NativeHeight;
    public const int TotalSize = OffPixels + PixelCount;

    public sealed class Publisher : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private long _sequence;

        public Publisher(string name = DefaultName)
        {
            _mmf = MemoryMappedFile.CreateOrOpen(name, TotalSize, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

            _view.Write(OffMagic, Magic);
            _view.Write(OffWidth, LipFrame.NativeWidth);
            _view.Write(OffHeight, LipFrame.NativeHeight);
            _view.Write(OffPublisherPid, Environment.ProcessId);
        }

        public void Publish(LipFrame frame, TrackerHealth health,
            IReadOnlyList<float>? weights = null)
        {
            // Odd sequence marks a write in progress.
            long seq = Interlocked.Increment(ref _sequence) * 2 - 1;
            _view.Write(OffSequence, seq);
            Thread.MemoryBarrier();

            _view.WriteArray(OffPixels, frame.Luma, 0, frame.Luma.Length);

            if (weights is not null)
            {
                int count = Math.Min(WeightCount, weights.Count);
                for (int i = 0; i < count; i++)
                    _view.Write(OffWeights + i * sizeof(float), weights[i]);
                _view.Write(OffHasWeights, 1);
            }
            else
            {
                _view.Write(OffHasWeights, 0);
            }

            _view.Write(OffTimestamp, frame.TimestampMs);
            _view.Write(OffState, (int)health.State);
            _view.Write(OffFps, health.Fps);
            _view.Write(OffFrames, health.FramesReceived);
            _view.Write(OffRecoveries, health.Recoveries);

            Thread.MemoryBarrier();
            _view.Write(OffSequence, seq + 1);
        }

        /// <summary>Publishes stats only, so viewers still see state changes while stalled.</summary>
        public void PublishHealth(TrackerHealth health)
        {
            _view.Write(OffState, (int)health.State);
            _view.Write(OffFps, health.Fps);
            _view.Write(OffFrames, health.FramesReceived);
            _view.Write(OffRecoveries, health.Recoveries);
        }

        public void Dispose()
        {
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    public sealed record Snapshot(
        int Width,
        int Height,
        TrackerState State,
        double TimestampMs,
        double Fps,
        long FramesReceived,
        long Recoveries,
        int PublisherPid,
        bool HasWeights);

    public sealed class Subscriber : IDisposable
    {
        private readonly string _name;
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _view;

        public Subscriber(string name = DefaultName) => _name = name;

        public bool IsConnected => _view is not null;

        /// <summary>
        /// True when a publisher exists but uses an incompatible layout -- i.e. the module and
        /// the viewer were built from different revisions and must be reinstalled together.
        /// </summary>
        public bool VersionMismatch { get; private set; }

        /// <summary>Attaches to a running publisher. Safe to call repeatedly.</summary>
        public bool TryConnect()
        {
            if (_view is not null) return true;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(_name, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.Read);

                int magic = _view.ReadInt32(OffMagic);
                if (magic != Magic)
                {
                    // A publisher is running but speaks a different layout: almost always a
                    // stale VRCFT module alongside a freshly built viewer. Say so plainly.
                    VersionMismatch = true;
                    Disconnect();
                    return false;
                }

                VersionMismatch = false;
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;   // No publisher running yet.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Disconnect()
        {
            _view?.Dispose(); _view = null;
            _mmf?.Dispose(); _mmf = null;
        }

        /// <summary>
        /// Copies the current frame. Returns null if no consistent read was possible.
        /// </summary>
        /// <summary>
        /// Copies the current frame, and the blendshape weights when <paramref name="weights"/>
        /// is supplied. Pass null for pixels to read stats and weights only -- useful when the
        /// consumer is not drawing the image.
        /// </summary>
        public Snapshot? TryRead(byte[]? destination, float[]? weights = null)
        {
            var view = _view;
            if (view is null) return null;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                long before = view.ReadInt64(OffSequence);
                if ((before & 1) != 0) { Thread.SpinWait(50); continue; } // write in progress

                Thread.MemoryBarrier();

                if (destination is not null)
                    view.ReadArray(OffPixels, destination, 0, Math.Min(destination.Length, PixelCount));

                bool hasWeights = view.ReadInt32(OffHasWeights) != 0;
                if (weights is not null && hasWeights)
                {
                    int count = Math.Min(WeightCount, weights.Length);
                    for (int i = 0; i < count; i++)
                        weights[i] = view.ReadSingle(OffWeights + i * sizeof(float));
                }

                var snapshot = new Snapshot(
                    view.ReadInt32(OffWidth),
                    view.ReadInt32(OffHeight),
                    (TrackerState)view.ReadInt32(OffState),
                    view.ReadDouble(OffTimestamp),
                    view.ReadDouble(OffFps),
                    view.ReadInt64(OffFrames),
                    view.ReadInt64(OffRecoveries),
                    view.ReadInt32(OffPublisherPid),
                    hasWeights);

                Thread.MemoryBarrier();
                if (view.ReadInt64(OffSequence) == before) return snapshot;
            }

            return null; // Torn every attempt; caller keeps the previous frame.
        }

        public void Dispose() => Disconnect();
    }
}
