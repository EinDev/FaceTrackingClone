using System.Diagnostics;
using FaceTrackingClone.Inference;
using FaceTrackingClone.Ipc;
using FaceTrackingClone.Vive;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;

namespace FaceTrackingClone.VrcftModule;

/// <summary>
/// VRCFaceTracking module for the VIVE Facial Tracker.
///
/// Talks straight to the camera over UVC and runs inference in-process. SRanipal is not used
/// for the lower face at all, so an SRanipal stall can no longer take the mouth down -- and the
/// capture pipeline recovers from its own stalls without the module (or VRCFT) restarting.
///
/// Expression only: eye tracking on the Vive Pro Eye is a Tobii vendor protocol with no public
/// interface, so it stays with the SRanipal module. Run both together.
/// </summary>
public sealed class ViveLipModule : ExtTrackingModule
{
    private ViveLipTracker? _tracker;
    private LipInference? _inference;
    private FrameShare.Publisher? _publisher;
    private LipFrame? _frame;

    /// <summary>
    /// Inference rate cap. The camera runs at 60Hz, but VRChat's OSC parameter rate is far
    /// below that, so inferring on every frame doubles CPU for output nobody receives. Halving
    /// the rate matters here specifically: CPU contention is what provokes the framedrop stalls
    /// this module exists to survive.
    ///
    /// Tunable via FTCLONE_INFERENCE_HZ / FTCLONE_THREADS, because the thread count is a real
    /// trade-off rather than a clear win: more threads cut latency but raise total CPU
    /// (measured 17.5ms CPU/inference at 1 thread vs 41.6ms at 4).
    /// </summary>
    private static readonly double InferenceHz = ReadEnv("FTCLONE_INFERENCE_HZ", 30, 1, 60);

    private static readonly int InferenceThreads = (int)ReadEnv("FTCLONE_THREADS", 1, 1, 8);

    /// <summary>
    /// Camera preview rate for VRCFT's own debug view. Kept low deliberately: the frame is
    /// 400x400 BGRA (640KB) and crosses the sandbox boundary over UDP, so streaming it at
    /// capture rate would cost far more than the tracking itself. 0 disables it.
    /// </summary>
    /// <summary>
    /// Camera preview for VRCFT's own Hardware Debug card. Defaults to OFF because
    /// VRCFaceTracking.ModuleProcess never forwards image data to the host in 5.4.5 -- the
    /// packet type exists in Core but nothing sends it, so the card cannot display a
    /// sandboxed module's frames. Building the buffer anyway would be pure waste.
    /// </summary>
    private static readonly double PreviewHz = ReadEnv("FTCLONE_PREVIEW_HZ", 0, 0, 30);

    /// <summary>Auto-launch the standalone viewer window. Set FTCLONE_VIEWER=0 to disable.</summary>
    private static readonly bool LaunchViewer = ReadEnv("FTCLONE_VIEWER", 1, 0, 1) > 0;

    private readonly double _minInferenceIntervalMs = 1000.0 / InferenceHz;

    private static double ReadEnv(string name, double fallback, double min, double max)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (raw is null || !double.TryParse(raw, out double value)) return fallback;
        return Math.Clamp(value, min, max);
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastLogMs;
    private double _lastInferenceMs;
    private double _lastPreviewMs;
    private long _lastSequence = -1;
    private long _updates;
    private long _skippedDuplicates;

    /// <summary>BGRA8 buffer handed to VRCFT; mutated in place once assigned.</summary>
    private byte[]? _previewBuffer;

    private Process? _viewerProcess;

    /// <summary>
    /// Lower face only; eye tracking on this headset is a Tobii vendor protocol we cannot reach.
    ///
    /// Logged because VRCFT reports the host's AND-ed capability flags rather than what the
    /// module returned, which makes a mismatch impossible to diagnose from its log alone.
    /// </summary>
    public override (bool SupportsEye, bool SupportsExpression) Supported
    {
        get
        {
            Logger?.LogInformation("FaceTrackingClone: Supported -> eye=False expression=True");
            return (false, true);
        }
    }

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(
        bool eyeAvailable, bool expressionAvailable)
    {
        Logger.LogInformation(
            "FaceTrackingClone: Initialize(eyeAvailable={Eye}, expressionAvailable={Expression})",
            eyeAvailable, expressionAvailable);

        if (!expressionAvailable)
        {
            Logger.LogInformation(
                "FaceTrackingClone: expression tracking already claimed by another module.");
            return (false, false);
        }

        ModuleInformation.Name = "VIVE Facial Tracker (FaceTrackingClone)";
        ModuleInformation.UsingEye = false;
        ModuleInformation.UsingExpression = true;

        try
        {
            _inference = new LipInference(new LipInferenceOptions
            {
                ModelPath = ResolveModelPath(),
                Threads = InferenceThreads
            });
            Logger.LogInformation(
                "FaceTrackingClone: inference at {Hz}Hz on {Threads} thread(s).",
                InferenceHz, InferenceThreads);
        }
        catch (Exception ex)
        {
            Logger.LogError("FaceTrackingClone: could not load the inference model: {Message}",
                ex.Message);
            return (false, false);
        }

        _frame = new LipFrame();
        _tracker = new ViveLipTracker();

        try
        {
            _publisher = new FrameShare.Publisher();
        }
        catch (Exception ex)
        {
            // The live viewer is a convenience, never a requirement.
            Logger.LogWarning("FaceTrackingClone: frame sharing unavailable: {Message}", ex.Message);
        }

        // Advertise the camera preview so VRCFT renders it in its own hardware debug view.
        // Its UI writes ImageData straight into a WinUI WriteableBitmap.PixelBuffer, which is
        // BGRA8 -- hence 4 bytes per pixel rather than the greyscale the sensor produces.
        if (PreviewHz > 0)
        {
            _previewBuffer = new byte[LipFrame.NativeWidth * LipFrame.NativeHeight * 4];
            UnifiedTracking.LipImageData.SupportsImage = true;
            UnifiedTracking.LipImageData.ImageSize = (LipFrame.NativeWidth, LipFrame.NativeHeight);
            UnifiedTracking.LipImageData.ImageData = _previewBuffer;
            Logger.LogInformation("FaceTrackingClone: camera preview enabled at {Hz}Hz.", PreviewHz);
        }

        _tracker.Start();

        if (LaunchViewer) TryLaunchViewer();

        // The tracker opens asynchronously and heals itself, so a device that is not ready yet
        // is not a failure -- reporting success here lets it come online later without VRCFT
        // having to reinitialise the module.
        Logger.LogInformation("FaceTrackingClone: initialised, waiting for frames.");
        return (false, true);
    }

    public override void Update()
    {
        if (_tracker is null || _inference is null || _frame is null)
        {
            Thread.Sleep(100);
            return;
        }

        // The tracker delivers at 60Hz; polling faster only burns CPU.
        Thread.Sleep(10);

        if (!_tracker.TryGetLatestFrame(_frame)) return;

        // Re-running the model on a frame we already processed produces identical output.
        if (_frame.Sequence == _lastSequence)
        {
            _skippedDuplicates++;
            return;
        }

        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastInferenceMs < _minInferenceIntervalMs) return;
        _lastInferenceMs = now;
        _lastSequence = _frame.Sequence;

        try
        {
            var weights = _inference.Run(_frame);
            ShapeMapping.Apply(weights, UnifiedTracking.Data.Shapes);

            // Weights ride along with the frame so external viewers -- the WinForms window and
            // the Unity avatar preview -- can render what the model is actually producing.
            _publisher?.Publish(_frame, _tracker.Health, weights);
            _updates++;

            if (_previewBuffer is not null && now - _lastPreviewMs >= 1000.0 / PreviewHz)
            {
                _lastPreviewMs = now;
                WritePreview(_frame.Luma, _previewBuffer);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("FaceTrackingClone: inference failed: {Message}", ex.Message);
            Thread.Sleep(250);
            return;
        }

        LogHealthPeriodically();
    }

    /// <summary>
    /// Starts the standalone viewer alongside the module.
    ///
    /// VRCFT's own Hardware Debug card cannot show a sandboxed module's camera (its module host
    /// never forwards image frames), so this is the only way to get a live preview. The viewer
    /// attaches to our shared-memory feed rather than opening the camera, since a UVC device
    /// can only be owned by one process.
    /// </summary>
    private void TryLaunchViewer()
    {
        try
        {
            // Already running from a previous initialise, e.g. a module reload.
            if (_viewerProcess is { HasExited: false }) return;

            string moduleDir = Path.GetDirectoryName(typeof(ViveLipModule).Assembly.Location)
                               ?? AppContext.BaseDirectory;
            string exe = Path.Combine(moduleDir, "ftclone.exe");

            if (!File.Exists(exe))
            {
                Logger.LogInformation(
                    "FaceTrackingClone: viewer not installed alongside the module; skipping.");
                return;
            }

            _viewerProcess = Process.Start(new ProcessStartInfo(exe, "view --attach")
            {
                WorkingDirectory = moduleDir,
                UseShellExecute = false,
                CreateNoWindow = true   // suppress the console; only the viewer window shows
            });

            Logger.LogInformation("FaceTrackingClone: viewer launched (set FTCLONE_VIEWER=0 to disable).");
        }
        catch (Exception ex)
        {
            // A missing preview must never stop tracking from working.
            Logger.LogWarning("FaceTrackingClone: could not launch viewer: {Message}", ex.Message);
        }
    }

    private void StopViewer()
    {
        try
        {
            if (_viewerProcess is { HasExited: false })
            {
                _viewerProcess.Kill();
                _viewerProcess.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("FaceTrackingClone: could not stop viewer: {Message}", ex.Message);
        }
        finally
        {
            _viewerProcess?.Dispose();
            _viewerProcess = null;
        }
    }

    /// <summary>
    /// Expands the greyscale sensor frame into the BGRA8 layout VRCFT's preview expects.
    /// Alpha is opaque; B, G and R all take the luminance value.
    /// </summary>
    private static void WritePreview(byte[] luma, byte[] bgra)
    {
        int pixels = Math.Min(luma.Length, bgra.Length / 4);
        for (int i = 0, o = 0; i < pixels; i++, o += 4)
        {
            byte g = luma[i];
            bgra[o] = g;
            bgra[o + 1] = g;
            bgra[o + 2] = g;
            bgra[o + 3] = 255;
        }
    }

    /// <summary>
    /// Emits a health line every 30s. Deliberately noisy-on-recovery: the stalls this project
    /// exists to fix take hours to reproduce, so when one happens the log has to be enough to
    /// diagnose it without a repro.
    /// </summary>
    private void LogHealthPeriodically()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastLogMs < 30_000) return;
        _lastLogMs = now;

        TrackerHealth health = _tracker!.Health;
        Logger.LogInformation(
            "FaceTrackingClone: {State} fps={Fps:0.0} frames={Frames} recoveries={Recoveries} " +
            "inference={Inference:0.0}ms updates={Updates} skipped={Skipped}",
            health.State, health.Fps, health.FramesReceived, health.Recoveries,
            _inference!.LastInferenceMs, _updates, _skippedDuplicates);
    }

    public override void Teardown()
    {
        Logger.LogInformation("FaceTrackingClone: shutting down.");

        // Before the tracker, so the viewer is not left attached to a dead feed.
        StopViewer();

        _tracker?.Dispose();
        _tracker = null;

        _inference?.Dispose();
        _inference = null;

        _publisher?.Dispose();
        _publisher = null;

        _frame = null;
    }

    /// <summary>
    /// Modules are loaded from their own directory, so the model sits beside the DLL rather
    /// than beside VRCFaceTracking.exe.
    /// </summary>
    private static string ResolveModelPath()
    {
        string moduleDir = Path.GetDirectoryName(typeof(ViveLipModule).Assembly.Location)
                           ?? AppContext.BaseDirectory;
        return Path.Combine(moduleDir, "models", "babble.onnx");
    }
}
