using System.Diagnostics;
using System.Runtime.InteropServices;
using FaceTrackingClone.Imaging;
using FaceTrackingClone.Inference;
using FaceTrackingClone.Interop;
using FaceTrackingClone.Ipc;
using FaceTrackingClone.Ui;
using FaceTrackingClone.Vive;

namespace FaceTrackingClone;

internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";

        Mf.Check(Mf.MFStartup(Mf.MF_VERSION, Mf.MFSTARTUP_NOSOCKET), "MFStartup");
        try
        {
            return command switch
            {
                "probe" => Probe(),
                "grab" => Grab(args),
                "watch" => Watch(args),
                "view" => View(args),
                "model" => InspectModel(args),
                "infer" => Infer(args),
                "bench" => Bench(args),
                _ => Usage(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            if (ex is not MediaFoundationException && ex is not InvalidOperationException)
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            Mf.MFShutdown();
        }
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine("Usage: ftclone probe            - enumerate devices, formats, openability");
        Console.Error.WriteLine("       ftclone grab [frames]    - capture frames and report statistics");
        Console.Error.WriteLine("       ftclone watch [seconds]  - run the self-healing driver and report health");
        Console.Error.WriteLine("         --allow-device-reset     permit PnP device restart (needs admin)");
        Console.Error.WriteLine("       ftclone view             - live video window with stats");
        Console.Error.WriteLine("         --attach                 view a feed already owned by another process");
        Console.Error.WriteLine("       ftclone model [path]     - print ONNX model input/output metadata");
        Console.Error.WriteLine("       ftclone infer [seconds]  - live blendshapes from the tracker");
        Console.Error.WriteLine("       ftclone bench [iters]    - time inference at 1..4 threads (no camera needed)");
        return 2;
    }

    // -----------------------------------------------------------------------
    // bench: measures inference cost at different thread counts. Works without
    // the camera, so it can be run while VRCFT owns the device.
    // -----------------------------------------------------------------------
    private static int Bench(string[] args)
    {
        int iterations = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 200;

        // A synthetic frame is fine: inference cost depends on tensor shape, not content.
        var frame = new LipFrame();
        var random = new Random(1234);
        random.NextBytes(frame.Luma);

        Console.WriteLine($"Timing {iterations} inferences per configuration...");
        Console.WriteLine();
        Console.WriteLine($"  {"threads",-8} {"mean ms",9} {"p95 ms",9} {"est. CPU @30Hz",16}");
        Console.WriteLine($"  {new string('-', 46)}");

        foreach (int threads in new[] { 1, 2, 4 })
        {
            using var inference = new LipInference(new LipInferenceOptions
            {
                Threads = threads,
                Smooth = false
            });

            // Warm up: the first few runs include graph setup and JIT.
            for (int i = 0; i < 20; i++) inference.Run(frame);

            var samples = new List<double>(iterations);
            for (int i = 0; i < iterations; i++)
            {
                inference.Run(frame);
                samples.Add(inference.LastInferenceMs);
            }

            samples.Sort();
            double mean = samples.Average();
            double p95 = samples[(int)(samples.Count * 0.95)];

            Console.WriteLine($"  {threads,-8} {mean,9:0.00} {p95,9:0.00} " +
                              $"{mean * 30 / 10,15:0.0}%");
        }

        Console.WriteLine();
        Console.WriteLine("  'est. CPU @30Hz' is single-core percentage for 30 inferences/sec,");
        Console.WriteLine("  ignoring the extra cores a multi-threaded run occupies in parallel.");
        return 0;
    }

    // -----------------------------------------------------------------------
    // infer: full pipeline end to end -- capture, inference, live weights.
    // Used to confirm the model actually tracks a real face before wiring it
    // into VRCFaceTracking.
    // -----------------------------------------------------------------------
    private static int Infer(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], out double s) ? s : double.MaxValue;

        using var inference = new LipInference();
        using var tracker = new ViveLipTracker();
        using var publisher = new FrameShare.Publisher();

        var frame = new LipFrame();
        tracker.Start();

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        Console.WriteLine("Running inference. Ctrl+C to stop.");
        Console.WriteLine("Open your mouth / smile / puff your cheeks and watch the values move.");
        Console.WriteLine();

        var started = Stopwatch.StartNew();
        while (!stop.IsSet && started.Elapsed.TotalSeconds < seconds)
        {
            stop.Wait(200);

            if (!tracker.TryGetLatestFrame(frame))
            {
                Console.WriteLine($"  {tracker.Health.State} - no frames yet");
                continue;
            }

            var weights = inference.Run(frame);
            publisher.Publish(frame, tracker.Health, weights);

            // Show the strongest shapes; a neutral face should be near-silent.
            var top = weights
                .Select((w, i) => (Weight: w, Index: i))
                .Where(x => x.Weight > 0.15f && x.Index != (int)BabbleShape.JawOpen)
                .OrderByDescending(x => x.Weight)
                .Take(6)
                .Select(x => $"{BabbleShapes.NameOf(x.Index)}={x.Weight:0.00}");

            string summary = string.Join("  ", top);
            Console.WriteLine($"  [{inference.LastInferenceMs,5:0.0}ms] " +
                              $"jawOpen={weights[(int)BabbleShape.JawOpen]:0.00}  " +
                              (summary.Length > 0 ? summary : "(neutral)"));
        }

        tracker.Stop();
        return 0;
    }

    /// <summary>
    /// Reports the inference model's tensor shapes. The blendshape mapping depends entirely on
    /// these, so they are read from the model rather than assumed.
    /// </summary>
    private static int InspectModel(string[] args)
    {
        string path = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "models", "babble.onnx");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Model not found: {path}");
            return 1;
        }

        Console.WriteLine($"Model: {path}");
        Console.WriteLine($"Size:  {new FileInfo(path).Length / 1024.0 / 1024.0:0.00} MB");
        Console.WriteLine();

        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(path);

        Console.WriteLine("Inputs:");
        foreach (var kv in session.InputMetadata)
        {
            Console.WriteLine($"  {kv.Key}  type={kv.Value.ElementDataType} " +
                              $"shape=[{string.Join(",", kv.Value.Dimensions)}]");
        }

        Console.WriteLine("Outputs:");
        foreach (var kv in session.OutputMetadata)
        {
            Console.WriteLine($"  {kv.Key}  type={kv.Value.ElementDataType} " +
                              $"shape=[{string.Join(",", kv.Value.Dimensions)}]");
        }

        if (session.ModelMetadata.CustomMetadataMap.Count > 0)
        {
            Console.WriteLine("Metadata:");
            foreach (var kv in session.ModelMetadata.CustomMetadataMap)
                Console.WriteLine($"  {kv.Key} = {kv.Value}");
        }

        return 0;
    }

    // -----------------------------------------------------------------------
    // view: desktop window showing the live feed and pipeline stats.
    //
    // If the camera is free this process drives it and publishes frames to shared memory.
    // If something else already owns it (the driver, or the VRCFT module) it attaches to that
    // publisher instead -- a UVC device cannot be opened twice.
    // -----------------------------------------------------------------------
    private static int View(string[] args)
    {
        bool forceAttach = args.Contains("--attach", StringComparer.OrdinalIgnoreCase);

        ViveLipTracker? tracker = null;
        FrameShare.Publisher? publisher = null;
        FrameShare.Subscriber? subscriber = null;
        LipInference? inference = null;

        if (forceAttach)
        {
            subscriber = new FrameShare.Subscriber();
            Console.WriteLine("Attaching to an existing feed...");
        }
        else
        {
            // Probe for an existing publisher first so two viewers do not fight over the device.
            var probe = new FrameShare.Subscriber();
            if (probe.TryConnect())
            {
                subscriber = probe;
                Console.WriteLine("Another process owns the tracker; attaching to its feed.");
            }
            else
            {
                probe.Dispose();
                tracker = new ViveLipTracker();
                publisher = new FrameShare.Publisher();
                tracker.Start();
                Console.WriteLine("Driving the tracker directly and publishing frames.");

                // Owner mode has no module feeding it weights, so run the model here to drive
                // the face render. Absent model = camera and stats only, which is still useful.
                try
                {
                    inference = new LipInference();
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine("No model found; face render disabled (run tools/fetch-model.ps1).");
                }
            }
        }

        // WinForms requires an STA thread; Media Foundation wants MTA, so the UI gets its own.
        var uiThread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ViewerForm(tracker, publisher, subscriber, inference));
        })
        {
            Name = "ftclone-ui",
            IsBackground = false
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();

        return 0;
    }

    // -----------------------------------------------------------------------
    // watch: run the real driver and print health once a second. This is the
    // soak test -- unplug the tracker, start SRanipal, stall the machine, and
    // the driver should recover without the process being restarted.
    // -----------------------------------------------------------------------
    private static int Watch(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], out double s) ? s : double.MaxValue;
        bool allowReset = args.Contains("--allow-device-reset", StringComparer.OrdinalIgnoreCase);

        if (allowReset && !DeviceReset.IsElevated())
            Console.WriteLine("WARNING: --allow-device-reset given but not running elevated; reset will fail.");

        using var tracker = new ViveLipTracker(new ViveLipTrackerOptions
        {
            AllowDeviceReset = allowReset
        });

        // Publish frames so `ftclone view --attach` can watch this driver live.
        using var publisher = new FrameShare.Publisher();
        tracker.FrameReceived += frame => publisher.Publish(frame, tracker.Health);

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        tracker.Start();
        Console.WriteLine("Running. Ctrl+C to stop.");
        Console.WriteLine();

        var started = Stopwatch.StartNew();
        while (!stop.IsSet && started.Elapsed.TotalSeconds < seconds)
        {
            stop.Wait(1000);
            Console.WriteLine($"  {tracker.Health}");
        }

        tracker.Stop();

        var final = tracker.Health;
        Console.WriteLine();
        Console.WriteLine($"Stopped after {started.Elapsed.TotalSeconds:0}s: " +
                          $"{final.FramesReceived} frames, {final.Recoveries} recoveries.");
        return 0;
    }

    // -----------------------------------------------------------------------
    // probe: what capture devices exist, can they be opened, and what can the
    // lip tracker actually do?
    // -----------------------------------------------------------------------
    private static int Probe()
    {
        Console.WriteLine("== Media Foundation video capture devices ==");
        var devices = CaptureDevices.EnumerateVideo();
        try
        {
            if (devices.Count == 0)
            {
                Console.WriteLine("  (none)");
                return 1;
            }

            // Open every device, not just the tracker: if they all fail the same way the
            // problem is our interop, if only the tracker fails it is the device.
            foreach (var d in devices)
            {
                string marker = d.IsViveFacialTracker ? "  <== VIVE FACIAL TRACKER" : "";
                Console.WriteLine($"  {d.FriendlyName}{marker}");
                Console.WriteLine($"      {d.SymbolicLink}");

                int hr = d.TryOpen(out IMFMediaSource? src);
                if (hr < 0)
                {
                    Console.WriteLine($"      open: FAILED 0x{hr:X8}{DescribeOpenFailure(hr)}");
                }
                else
                {
                    Console.WriteLine("      open: OK");
                    Marshal.ReleaseComObject(src!);
                    d.ShutdownActivated();
                }
            }

            var tracker = devices.FirstOrDefault(d => d.IsViveFacialTracker);
            if (tracker is null)
            {
                Console.WriteLine();
                Console.WriteLine("VIVE Facial Tracker not found among capture devices.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("== Native media types advertised by the tracker ==");

            IMFMediaSource source = tracker.Open();
            IMFSourceReader? reader = null;
            try
            {
                Mf.Check(Mf.MFCreateSourceReaderFromMediaSource(source, null, out reader),
                    "MFCreateSourceReaderFromMediaSource");

                var types = CaptureDevices.EnumerateNativeMediaTypes(reader);
                if (types.Count == 0)
                {
                    Console.WriteLine("  (device advertised no media types)");
                    return 1;
                }

                foreach (var t in types) Console.WriteLine($"  {t}");
            }
            finally
            {
                if (reader is not null) Marshal.ReleaseComObject(reader);
                Marshal.ReleaseComObject(source);
                tracker.ShutdownActivated();
            }

            Console.WriteLine();
            Console.WriteLine("Next: `ftclone grab 120` to test whether frames actually flow.");
            return 0;
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    private static string DescribeOpenFailure(int hr) => hr switch
    {
        unchecked((int)0x80070005) =>
            "  (E_ACCESSDENIED - another process holds it, or Windows camera privacy blocks it)",
        unchecked((int)0xC00D36E6) => "  (MF_E_ATTRIBUTENOTFOUND)",
        unchecked((int)0x8007001F) => "  (ERROR_GEN_FAILURE - device wedged)",
        unchecked((int)0x80070002) => "  (ERROR_FILE_NOT_FOUND - device disappeared)",
        _ => ""
    };

    // -----------------------------------------------------------------------
    // grab: the actual feasibility test. Do frames arrive, and do they contain
    // a real IR image rather than black? This determines whether the vendor
    // 0x14 activation sequence is required.
    // -----------------------------------------------------------------------
    private static int Grab(string[] args)
    {
        int wanted = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 120;

        var tracker = CaptureDevices.OpenViveFacialTracker(out var all);
        try
        {
            Console.WriteLine($"Device: {tracker.FriendlyName}");

            IMFMediaSource source = tracker.Open();
            IMFSourceReader? reader = null;
            try
            {
                Mf.Check(Mf.MFCreateSourceReaderFromMediaSource(source, null, out reader),
                    "MFCreateSourceReaderFromMediaSource");

                var types = CaptureDevices.EnumerateNativeMediaTypes(reader);

                // Prefer the documented native mode: 400x400 YUY2 @60. Fall back to the largest
                // advertised mode so we still learn something if the device differs.
                var chosen = types.FirstOrDefault(t => t is { Width: 400, Height: 400 }
                                                       && t.SubtypeName == "YUY2")
                             ?? types.OrderByDescending(t => (long)t.Width * t.Height).FirstOrDefault()
                             ?? throw new InvalidOperationException("Device advertised no media types.");

                Console.WriteLine($"Selected format: {chosen}");

                Mf.Check(reader.GetNativeMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    chosen.Index, out IMFMediaType? mt), "GetNativeMediaType");
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

                return CaptureLoop(reader, chosen, wanted);
            }
            finally
            {
                if (reader is not null) Marshal.ReleaseComObject(reader);
                Marshal.ReleaseComObject(source);
                tracker.ShutdownActivated();
            }
        }
        finally
        {
            foreach (var d in all) d.Dispose();
        }
    }

    private static int CaptureLoop(IMFSourceReader reader, MediaTypeInfo format, int wanted)
    {
        int width = (int)format.Width, height = (int)format.Height;
        var luma = new byte[width * height];

        string outDir = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(outDir);

        var sw = Stopwatch.StartNew();
        var gaps = new List<double>(wanted);
        double lastMs = 0;
        int received = 0, empty = 0, errors = 0;
        double globalMin = 255, globalMax = 0, globalMean = 0;
        int firstBufferLength = 0;

        Console.WriteLine($"Capturing {wanted} frames...");

        while (received < wanted)
        {
            int hr = reader.ReadSampleSync(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                out uint flags, out _, out IMFSample? sample);

            if (hr < 0)
            {
                errors++;
                Console.WriteLine($"  ReadSample error 0x{hr:X8} after {received} frames");
                break;
            }
            if ((flags & Mf.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                Console.WriteLine("  end of stream");
                break;
            }
            if (sample is null) { empty++; continue; }

            try
            {
                Mf.Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer? buffer),
                    "ConvertToContiguousBuffer");
                try
                {
                    Mf.Check(buffer!.Lock(out IntPtr data, out _, out uint length), "Lock");
                    try
                    {
                        if (firstBufferLength == 0) firstBufferLength = (int)length;
                        ExtractLuma(data, (int)length, format.SubtypeName, width, height, luma);
                    }
                    finally { buffer.Unlock(); }
                }
                finally { if (buffer is not null) Marshal.ReleaseComObject(buffer); }
            }
            finally { Marshal.ReleaseComObject(sample); }

            double nowMs = sw.Elapsed.TotalMilliseconds;
            if (received > 0) gaps.Add(nowMs - lastMs);
            lastMs = nowMs;

            var (min, max, mean) = Stats(luma);
            globalMin = Math.Min(globalMin, min);
            globalMax = Math.Max(globalMax, max);
            globalMean += mean;

            if (received is 0 or 30 or 60)
            {
                string path = Path.Combine(outDir, $"frame_{received:D4}.bmp");
                BmpWriter.WriteGrey8(path, luma, width, height);
                Console.WriteLine($"  saved {path}  (min={min} max={max} mean={mean:0.0})");
            }

            received++;
        }

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("== Results ==");
        Console.WriteLine($"  frames received : {received} (empty reads {empty}, errors {errors})");
        Console.WriteLine($"  buffer bytes    : {firstBufferLength} " +
                          $"(expected {width * height * 2} for {width}x{height} YUY2)");
        Console.WriteLine($"  elapsed         : {sw.Elapsed.TotalSeconds:0.00}s");
        if (received > 1)
            Console.WriteLine($"  effective fps   : {(received - 1) / (lastMs / 1000.0):0.0}");
        if (gaps.Count > 0)
        {
            gaps.Sort();
            Console.WriteLine($"  frame gap ms    : min {gaps[0]:0.0} / " +
                              $"median {gaps[gaps.Count / 2]:0.0} / max {gaps[^1]:0.0}");
        }
        if (received > 0)
        {
            globalMean /= received;
            Console.WriteLine($"  luma min/max    : {globalMin} / {globalMax}");
            Console.WriteLine($"  luma mean       : {globalMean:0.0}");
        }

        Console.WriteLine();
        Console.WriteLine("== Verdict ==");
        if (received == 0)
        {
            Console.WriteLine("  NO FRAMES. The device opened but never delivered a sample.");
            Console.WriteLine("  -> vendor activation (Babble's 0x14 request) is required.");
            return 1;
        }
        if (globalMax <= 2)
        {
            Console.WriteLine("  FRAMES ARE BLACK. Streaming works but the IR illuminator/sensor is off.");
            Console.WriteLine("  -> vendor activation (0x14) and/or exposure+gain setup (0xab) required.");
            return 1;
        }
        Console.WriteLine("  FRAMES CONTAIN IMAGE DATA. Plain UVC capture is sufficient --");
        Console.WriteLine("  no vendor activation needed. Open the saved BMPs to confirm they show");
        Console.WriteLine("  your lower face.");
        return 0;
    }

    /// <summary>
    /// Pulls the luminance plane out of whatever the device handed us. YUY2 is the documented
    /// native format (Y in every even byte); other packings are handled so a format surprise
    /// still produces a viewable image rather than an exception.
    /// </summary>
    private static void ExtractLuma(IntPtr data, int length, string subtype,
        int width, int height, byte[] dest)
    {
        Array.Clear(dest);
        unsafe
        {
            byte* src = (byte*)data;
            switch (subtype)
            {
                case "YUY2":
                case "UYVY":
                {
                    int yOffset = subtype == "YUY2" ? 0 : 1;
                    int pixels = Math.Min(width * height, (length - yOffset + 1) / 2);
                    for (int i = 0; i < pixels; i++) dest[i] = src[i * 2 + yOffset];
                    break;
                }
                case "NV12":
                case "YV12":
                case "I420":
                case "L8":
                {
                    int n = Math.Min(width * height, length);
                    for (int i = 0; i < n; i++) dest[i] = src[i];
                    break;
                }
                default:
                {
                    // Unknown packing: take the first channel of a best-guess stride so the
                    // image is at least inspectable.
                    int bpp = Math.Max(1, length / Math.Max(1, width * height));
                    int n = Math.Min(width * height, length / bpp);
                    for (int i = 0; i < n; i++) dest[i] = src[i * bpp];
                    break;
                }
            }
        }
    }

    private static (byte Min, byte Max, double Mean) Stats(byte[] buf)
    {
        byte min = 255, max = 0;
        long sum = 0;
        foreach (byte b in buf)
        {
            if (b < min) min = b;
            if (b > max) max = b;
            sum += b;
        }
        return (min, max, (double)sum / buf.Length);
    }
}
