using System.Diagnostics;
using System.Runtime.InteropServices;
using FaceTrackingClone.Interop;

namespace FaceTrackingClone.Vive;

internal sealed class ViveLipTrackerOptions
{
    /// <summary>No frame for this long means the stream is stalled: flush and re-arm.</summary>
    public double SoftStallMs { get; init; } = 400;

    /// <summary>No frame for this long means a flush will not save it: rebuild the reader.</summary>
    public double HardStallMs { get; init; } = 1500;

    /// <summary>Consecutive failed rebuilds before escalating to a PnP device restart.</summary>
    public int RebuildsBeforeDeviceReset { get; init; } = 3;

    /// <summary>Device restart needs administrator rights, so it is opt-in.</summary>
    public bool AllowDeviceReset { get; init; }

    /// <summary>Base backoff between reopen attempts; grows geometrically with failures.</summary>
    public double ReconnectDelayMs { get; init; } = 300;

    /// <summary>Ceiling for the backoff. Kept small so a mid-session stall clears quickly.</summary>
    public double MaxReconnectDelayMs { get; init; } = 3000;
}

/// <summary>
/// Self-healing capture driver for the VIVE Facial Tracker, talking straight to the UVC
/// device through Media Foundation. SRanipal is not involved at any point.
///
/// The whole design goal is that a stall never requires a process restart. Three things make
/// that possible:
///
///  1. The source reader runs in *asynchronous* mode. Synchronous ReadSample blocks forever on
///     a wedged device, which would leave the watchdog unable to do anything -- exactly the
///     failure mode being fixed. In async mode no thread is ever stuck inside MF.
///  2. A watchdog thread owns all recovery, escalating through flush -> rebuild -> device reset.
///  3. Recovery tears down through IMFActivate::ShutdownObject, because the activate caches the
///     media source and a plain IMFMediaSource::Shutdown leaves the cache poisoned.
/// </summary>
internal sealed class ViveLipTracker : IDisposable
{
    private readonly ViveLipTrackerOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();

    // Double-buffered frames: the callback fills _back, then swaps under the lock, so readers
    // never observe a torn frame and the callback never allocates.
    private LipFrame _front = new();
    private LipFrame _back = new();

    private CaptureDeviceHandle? _device;
    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private SourceReaderCallback? _callback;

    private Thread? _watchdog;
    private volatile bool _running;

    /// <summary>
    /// Set by the callback when the stream faults. Lets the watchdog rebuild immediately instead
    /// of waiting out the stall timer, which is the difference between a ~2s and a ~4s recovery.
    /// </summary>
    private volatile bool _streamFaulted;

    private long _framesReceived;
    private long _recoveries;
    private long _sequence;
    private double _lastFrameMs;
    private double _fps;
    private string? _lastError;
    private TrackerState _state = TrackerState.Stopped;

    public ViveLipTracker(ViveLipTrackerOptions? options = null)
        => _options = options ?? new ViveLipTrackerOptions();

    /// <summary>Raised on the MF callback thread for each frame. Keep handlers cheap.</summary>
    public event Action<LipFrame>? FrameReceived;

    public TrackerHealth Health
    {
        get
        {
            lock (_sync)
            {
                double age = _lastFrameMs <= 0 ? -1 : _clock.Elapsed.TotalMilliseconds - _lastFrameMs;
                return new TrackerHealth(_state, _framesReceived, _recoveries, _fps, age, _lastError);
            }
        }
    }

    /// <summary>Copies the most recent frame. Returns false if nothing has arrived yet.</summary>
    public bool TryGetLatestFrame(LipFrame into)
    {
        lock (_sync)
        {
            if (_framesReceived == 0) return false;
            into.CopyFrom(_front);
            return true;
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        SetState(TrackerState.Starting);

        _watchdog = new Thread(WatchdogLoop)
        {
            Name = "ftclone-lip-watchdog",
            IsBackground = true,
            // Above normal so a saturated GPU/CPU during a framedrop cannot starve recovery.
            Priority = ThreadPriority.AboveNormal
        };
        _watchdog.Start();
    }

    public void Stop()
    {
        _running = false;
        _watchdog?.Join(TimeSpan.FromSeconds(5));
        _watchdog = null;
        Close();
        SetState(TrackerState.Stopped);
    }

    public void Dispose() => Stop();

    // -----------------------------------------------------------------------
    // Watchdog: the only thread allowed to open, close, or rebuild the pipeline.
    // -----------------------------------------------------------------------
    private void WatchdogLoop()
    {
        // One counter, one meaning: consecutive recovery cycles that failed to produce a frame.
        // Failing to open and opening-then-faulting are the same kind of failure and must not be
        // counted separately, or the backoff compounds and recovery goes quiet for tens of seconds.
        int failedCycles = 0;
        long framesAtOpen = 0;

        while (_running)
        {
            try
            {
                if (_reader is null)
                {
                    if (!TryOpen())
                    {
                        SetState(TrackerState.Disconnected);
                        failedCycles++;
                        Escalate(failedCycles);
                        Sleep(Backoff(failedCycles));
                        continue;
                    }

                    // Not Streaming yet: opening succeeds even when another process holds the
                    // device and the stream faults a moment later. Only real frames prove
                    // recovery worked, so the counter is reset below, not here.
                    framesAtOpen = Interlocked.Read(ref _framesReceived);
                    SetState(TrackerState.Starting);
                }

                Sleep(100);

                if (Interlocked.Read(ref _framesReceived) > framesAtOpen)
                    failedCycles = 0;

                double age;
                lock (_sync)
                {
                    age = _lastFrameMs <= 0
                        ? _clock.Elapsed.TotalMilliseconds - _openedAtMs
                        : _clock.Elapsed.TotalMilliseconds - _lastFrameMs;
                }

                // A hard fault will never resolve by waiting, so skip straight to a rebuild.
                bool faulted = _streamFaulted;

                if (!faulted && age < _options.SoftStallMs) continue;

                if (!faulted && age < _options.HardStallMs)
                {
                    // Level 1: the reader is alive but the queue dried up. A flush re-arms it
                    // and is cheap enough to be harmless if the stall was transient.
                    SetState(TrackerState.Stalled);
                    Log($"soft stall ({age:0}ms) - flushing");
                    lock (_sync)
                    {
                        _reader?.Flush(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
                        _callback?.RequestNextSample();
                    }
                    Sleep(150);
                    continue;
                }

                // Level 2: rebuild the entire pipeline.
                SetState(TrackerState.Stalled);
                failedCycles++;
                Interlocked.Increment(ref _recoveries);
                Log(faulted
                    ? $"stream faulted - rebuilding pipeline (cycle {failedCycles})"
                    : $"hard stall ({age:0}ms) - rebuilding pipeline (cycle {failedCycles})");
                _streamFaulted = false;

                Close();
                Trace("loop: closed");

                Escalate(failedCycles);
                Trace("loop: escalation checked");

                Sleep(Backoff(failedCycles));
                Trace("loop: backoff done");
            }
            catch (Exception ex)
            {
                Log($"watchdog error: {ex.Message}");
                Close();
                SetState(TrackerState.Faulted);
                failedCycles++;
                Sleep(Backoff(failedCycles));
            }
        }
    }

    /// <summary>
    /// Level 3: reopening is not clearing the fault, so the device itself is wedged.
    ///
    /// Fires on every Nth failed cycle and deliberately does NOT reset the caller's counter --
    /// that counter also drives the backoff, and zeroing it here would pin the retry interval at
    /// its minimum forever, hammering a device that some other process legitimately owns.
    /// </summary>
    private void Escalate(int failedCycles)
    {
        if (failedCycles <= 0 || failedCycles % _options.RebuildsBeforeDeviceReset != 0) return;

        if (!_options.AllowDeviceReset)
        {
            Log("device reset would trigger here (disabled; needs admin, pass --allow-device-reset)");
            return;
        }

        Log("escalating to PnP device restart");
        if (DeviceReset.TryRestartViveFacialTracker(out string? err))
            Log("device restarted");
        else
            Log($"device restart failed: {err}");
    }

    private double _openedAtMs;

    private bool TryOpen()
    {
        List<CaptureDeviceHandle> all;
        CaptureDeviceHandle device;
        Trace("TryOpen: enumerating devices");
        try
        {
            device = CaptureDevices.OpenViveFacialTracker(out all);
        }
        catch (InvalidOperationException ex)
        {
            _lastError = ex.Message;
            Trace($"TryOpen: enumerate failed: {ex.Message}");
            return false;
        }
        Trace("TryOpen: enumerated");

        // Only the tracker handle is kept; the rest are released immediately.
        foreach (var d in all) if (!ReferenceEquals(d, device)) d.Dispose();
        Trace("TryOpen: released other handles");

        try
        {
            Trace("TryOpen: activating media source");
            IMFMediaSource source = device.Open();
            Trace("TryOpen: media source activated");
            var callback = new SourceReaderCallback(this);

            Mf.Check(Mf.MFCreateAttributes(out IMFAttributes attrs, 1), "MFCreateAttributes");
            IMFSourceReader reader;
            try
            {
                Mf.Check(attrs.SetUnknown(MfGuids.MF_SOURCE_READER_ASYNC_CALLBACK, callback),
                    "SetUnknown(async callback)");
                Trace("TryOpen: creating source reader");
                Mf.Check(Mf.MFCreateSourceReaderFromMediaSource(source, attrs, out reader),
                    "MFCreateSourceReaderFromMediaSource");
                Trace("TryOpen: source reader created");
            }
            finally
            {
                Marshal.ReleaseComObject(attrs);
            }

            ConfigureFormat(reader);
            Trace("TryOpen: format configured");

            lock (_sync)
            {
                _device = device;
                _source = source;
                _reader = reader;
                _callback = callback;
                _openedAtMs = _clock.Elapsed.TotalMilliseconds;
                _lastFrameMs = 0;
                _lastError = null;
                _streamFaulted = false;
                callback.Attach(reader);
            }

            callback.RequestNextSample();
            Log("pipeline open");
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log($"open failed: {ex.Message}");
            try { device.ShutdownActivated(); } catch { /* device may be gone */ }
            device.Dispose();
            return false;
        }
    }

    private static void ConfigureFormat(IMFSourceReader reader)
    {
        var types = CaptureDevices.EnumerateNativeMediaTypes(reader);
        var chosen = types.FirstOrDefault(t => t is { Width: LipFrame.NativeWidth,
                                                      Height: LipFrame.NativeHeight }
                                               && t.SubtypeName == "YUY2")
                     ?? types.FirstOrDefault()
                     ?? throw new InvalidOperationException("Tracker advertised no media types.");

        Mf.Check(reader.GetNativeMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, chosen.Index,
            out IMFMediaType? mt), "GetNativeMediaType");
        try
        {
            Mf.Check(reader.SetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                IntPtr.Zero, mt!), "SetCurrentMediaType");
        }
        finally
        {
            if (mt is not null) Marshal.ReleaseComObject(mt);
        }

        Mf.Check(reader.SetStreamSelection(Mf.MF_SOURCE_READER_ALL_STREAMS, false),
            "SetStreamSelection(all=false)");
        Mf.Check(reader.SetStreamSelection(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true),
            "SetStreamSelection(video=true)");
    }

    /// <summary>
    /// Tears the pipeline down.
    ///
    /// MUST NOT be called while holding <see cref="_sync"/>. Releasing the source reader blocks
    /// until in-flight MF callbacks return, and those callbacks take <see cref="_sync"/> to
    /// publish their frame -- holding it here deadlocks the watchdog against the callback thread
    /// permanently, which is precisely the unrecoverable hang this driver exists to avoid.
    /// So: swap the fields out under the lock, then release outside it.
    /// </summary>
    private void Close()
    {
        SourceReaderCallback? callback;
        IMFSourceReader? reader;
        IMFMediaSource? source;
        CaptureDeviceHandle? device;

        Trace("Close: acquiring lock");
        lock (_sync)
        {
            callback = _callback;
            reader = _reader;
            source = _source;
            device = _device;

            _callback = null;
            _reader = null;
            _source = null;
            _device = null;
        }
        Trace("Close: fields swapped out");

        // Detach first so any callback that fires during teardown returns immediately
        // instead of contending for the lock.
        callback?.Detach();

        // EVERY teardown call runs on the worker, including ShutdownObject. An async ReadSample
        // issued against a device another process owns never completes, and both
        // IMFActivate::ShutdownObject and releasing the reader block waiting on it. Leaving any
        // one of them on the watchdog thread hangs recovery permanently.
        //
        // Ordering still matters inside the worker: shutting the source down first is what
        // un-caches it on the activate so the next open gets a fresh source.
        var releaser = new Thread(() =>
        {
            if (device is not null) { try { device.ShutdownActivated(); } catch { } }
            if (reader is not null) { try { Marshal.ReleaseComObject(reader); } catch { } }
            if (source is not null) { try { Marshal.ReleaseComObject(source); } catch { } }
            if (device is not null) { try { device.Dispose(); } catch { } }
        })
        {
            Name = "ftclone-lip-teardown",
            IsBackground = true
        };
        releaser.Start();
        Trace("Close: releaser started");

        if (!releaser.Join(TimeSpan.FromSeconds(2)))
            Log("teardown timed out - abandoning COM objects and continuing");

        Trace("Close: done");
    }

    // -----------------------------------------------------------------------
    // Frame intake, called on an MF worker thread.
    // -----------------------------------------------------------------------
    private void OnSample(IMFSample sample)
    {
        int hr = sample.ConvertToContiguousBuffer(out IMFMediaBuffer? buffer);
        if (hr < 0 || buffer is null) return;

        try
        {
            if (buffer.Lock(out IntPtr data, out _, out uint length) < 0) return;
            try
            {
                DecodeYuy2Luma(data, (int)length, _back.Luma);
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }

        double now = _clock.Elapsed.TotalMilliseconds;
        LipFrame delivered;

        lock (_sync)
        {
            _back.TimestampMs = now;
            _back.Sequence = ++_sequence;

            (_front, _back) = (_back, _front);
            delivered = _front;

            if (_lastFrameMs > 0)
            {
                double dt = now - _lastFrameMs;
                if (dt > 0)
                {
                    // Light exponential smoothing; raw per-frame rate is far too noisy to act on.
                    double instant = 1000.0 / dt;
                    _fps = _fps <= 0 ? instant : (_fps * 0.9) + (instant * 0.1);
                }
            }
            _lastFrameMs = now;
            _framesReceived++;
            if (_state != TrackerState.Streaming) _state = TrackerState.Streaming;
        }

        try { FrameReceived?.Invoke(delivered); }
        catch (Exception ex) { Log($"frame handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// YUY2 packs Y in every even byte. The tracker only ever produces greyscale, so the
    /// chroma bytes are discarded outright.
    /// </summary>
    private static void DecodeYuy2Luma(IntPtr data, int length, byte[] dest)
    {
        int pixels = Math.Min(dest.Length, length / 2);
        unsafe
        {
            byte* src = (byte*)data;
            for (int i = 0; i < pixels; i++) dest[i] = src[i * 2];
        }
    }

    private void SetState(TrackerState state)
    {
        lock (_sync) _state = state;
    }

    /// <summary>
    /// Retry delay that grows with consecutive failures. Without this, a device legitimately
    /// owned by another process (SRanipal, a browser tab) gets hammered with an open attempt
    /// every second forever. Capped so genuine recovery still happens promptly.
    /// </summary>
    private double Backoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return 0;
        double ms = _options.ReconnectDelayMs * Math.Pow(1.5, Math.Min(consecutiveFailures - 1, 5));
        // Capped low on purpose: this runs mid-session, so a stall must clear in seconds.
        return Math.Min(ms, _options.MaxReconnectDelayMs);
    }

    /// <summary>
    /// Sleeps in slices so Stop() stays responsive during long backoffs.
    ///
    /// Rounded to whole milliseconds up front: tracking the remainder as a double let a
    /// fractional tail (e.g. 0.5ms) truncate to a zero-length slice that never decremented,
    /// spinning the watchdog at 100% CPU forever. Integer math makes that unrepresentable.
    /// </summary>
    private void Sleep(double ms)
    {
        int remaining = (int)Math.Ceiling(ms);
        while (_running && remaining > 0)
        {
            int slice = Math.Min(100, remaining);
            Thread.Sleep(slice);
            remaining -= slice;
        }
    }

    private void Log(string message)
    {
        _lastError = message;
        Console.WriteLine($"[lip {_clock.Elapsed.TotalSeconds,7:0.00}s] {message}");
    }

    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("FTCLONE_TRACE") == "1";

    /// <summary>Fine-grained pipeline tracing, enabled with FTCLONE_TRACE=1.</summary>
    private void Trace(string message)
    {
        if (!TraceEnabled) return;
        Console.WriteLine($"[trace {_clock.Elapsed.TotalSeconds,7:0.00}s] {message}");
    }

    // -----------------------------------------------------------------------
    // COM callback object handed to the source reader.
    // -----------------------------------------------------------------------
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class SourceReaderCallback : IMFSourceReaderCallback
    {
        private readonly ViveLipTracker _owner;
        private IMFSourceReader? _reader;
        private volatile bool _detached;

        public SourceReaderCallback(ViveLipTracker owner) => _owner = owner;

        public void Attach(IMFSourceReader reader) => _reader = reader;

        public void Detach()
        {
            _detached = true;
            _reader = null;
        }

        public void RequestNextSample()
        {
            if (_detached) return;
            var reader = _reader;
            if (reader is null) return;

            try
            {
                int hr = reader.ReadSampleAsync(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
                if (hr < 0)
                {
                    // Never swallow this: a failed re-arm means frames silently stop forever,
                    // and the watchdog would keep rebuilding a pipeline that was never armed.
                    _owner.Log($"ReadSample re-arm failed: 0x{hr:X8}");
                }
            }
            catch (InvalidComObjectException)
            {
                // Reader was torn down underneath us; the watchdog will reopen.
            }
        }

        public int OnReadSample(int hrStatus, uint dwStreamIndex, uint dwStreamFlags,
            long llTimestamp, IMFSample? pSample)
        {
            if (_detached) return 0;

            try
            {
                if (hrStatus >= 0 && pSample is not null)
                    _owner.OnSample(pSample);
            }
            catch (Exception ex)
            {
                _owner.Log($"sample handler threw: {ex.Message}");
            }
            finally
            {
                if (pSample is not null)
                {
                    try { Marshal.ReleaseComObject(pSample); } catch { /* ignore */ }
                }
            }

            // Deliberately do NOT re-arm on error or end-of-stream; the watchdog owns recovery
            // and re-arming into a dead device would spin a worker thread at full speed.
            bool fatal = hrStatus < 0
                         || (dwStreamFlags & Mf.MF_SOURCE_READERF_ERROR) != 0
                         || (dwStreamFlags & Mf.MF_SOURCE_READERF_ENDOFSTREAM) != 0;

            if (!fatal)
            {
                RequestNextSample();
            }
            else
            {
                _owner._streamFaulted = true;
                _owner.Log($"stream fault: hr=0x{hrStatus:X8} flags=0x{dwStreamFlags:X}");
            }

            return 0;
        }

        public int OnFlush(uint dwStreamIndex) => 0;

        public int OnEvent(uint dwStreamIndex, IntPtr pEvent) => 0;
    }
}
