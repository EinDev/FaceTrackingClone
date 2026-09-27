using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FaceTrackingClone.Ipc;
using FaceTrackingClone.Vive;

namespace FaceTrackingClone.Ui;

/// <summary>
/// Live view of the facial tracker feed plus pipeline statistics.
///
/// Two modes, chosen automatically:
///  * Owner  - this process opened the camera and drives a ViveLipTracker, publishing frames.
///  * Attach - another process owns the camera; frames come from shared memory. This is what
///             makes it possible to watch the feed while the driver is actually in use.
///
/// Kept deliberately cheap. A monitoring window that burns a core is actively harmful here: CPU
/// contention is what provokes the stalls this project exists to survive, so the observer must
/// not perturb what it is observing.
/// </summary>
internal sealed class ViewerForm : Form
{
    /// <summary>Stats are for humans; refreshing them faster than this is wasted work.</summary>
    private const int StatsIntervalMs = 250;

    /// <summary>Luma stats sample every Nth pixel. Mean/min/max are indistinguishable at 1/16.</summary>
    private const int LumaSampleStride = 16;

    private readonly ViveLipTracker? _tracker;
    private readonly FrameShare.Publisher? _publisher;
    private readonly FrameShare.Subscriber? _subscriber;

    /// <summary>Owner mode only: the viewer runs the model itself so the face render has data.</summary>
    private readonly Inference.LipInference? _inference;
    private long _lastInferenceTicks;

    private readonly LipFrame _frame = new();
    private readonly byte[] _pixels = new byte[LipFrame.NativeWidth * LipFrame.NativeHeight];
    private readonly int[] _rowBuffer = new int[LipFrame.NativeWidth];
    private readonly int[] _leftViewColumns = new int[LipFrame.NativeWidth];
    private readonly int[] _greyToArgb = new int[256];
    private readonly Bitmap _bitmap;

    private readonly ImagePanel _picture;
    private readonly ShapePanel _shapes;
    private readonly FaceRenderPanel _face;
    private readonly Label _stats;
    private readonly float[] _weights = new float[FrameShare.WeightCount];
    private readonly System.Windows.Forms.Timer _timer;

    private readonly DateTime _startedUtc = DateTime.UtcNow;
    /// <summary>Poll interval while visible; ~30Hz.</summary>
    private const int ActiveIntervalMs = 33;

    /// <summary>Poll interval while minimised. Nothing is drawn, so this only keeps state fresh.</summary>
    private const int IdleIntervalMs = 500;

    private double _lastFrameStamp = -1;
    private long _lastStatsTicks;
    private string _lastStatsText = "";
    private bool _showLeftViewOnly = true;
    private bool _minimized;
    private bool _hasOwnWeights;

    // Cached stats, recomputed only when a new frame arrives.
    private byte _min, _max;
    private double _mean;

    public ViewerForm(ViveLipTracker? tracker, FrameShare.Publisher? publisher,
        FrameShare.Subscriber? subscriber, Inference.LipInference? inference = null)
    {
        _tracker = tracker;
        _publisher = publisher;
        _subscriber = subscriber;
        _inference = inference;

        // Precomputed column mapping for the left-view stretch, so the blit is pure copying.
        for (int x = 0; x < LipFrame.NativeWidth; x++)
            _leftViewColumns[x] = x * LipFrame.EyeWidth / LipFrame.NativeWidth;

        Text = "FaceTrackingClone - VIVE Facial Tracker";
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.Gainsboro;
        ClientSize = new Size(1300, 480);
        MinimumSize = new Size(820, 400);
        StartPosition = FormStartPosition.CenterScreen;

        // 32bpp rather than 8bpp indexed: GDI+ scales indexed bitmaps through a slow conversion
        // path, and that repaint was the bulk of the viewer's CPU. 640KB is a fine trade.
        for (int i = 0; i < 256; i++)
            _greyToArgb[i] = unchecked((int)0xFF000000) | (i << 16) | (i << 8) | i;

        _bitmap = new Bitmap(LipFrame.NativeWidth, LipFrame.NativeHeight,
            PixelFormat.Format32bppPArgb);

        _picture = new ImagePanel(_bitmap) { Dock = DockStyle.Fill };
        _picture.Click += (_, _) =>
        {
            _showLeftViewOnly = !_showLeftViewOnly;
            _lastFrameStamp = -1;  // force a redraw with the new framing
        };

        _stats = new Label
        {
            Dock = DockStyle.Right,
            Width = 290,
            Padding = new Padding(12),
            Font = new Font(FontFamily.GenericMonospace, 9f),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(18, 18, 22),
            TextAlign = ContentAlignment.TopLeft,
            UseCompatibleTextRendering = false
        };

        _shapes = new ShapePanel { Dock = DockStyle.Right, Width = 300 };
        _face = new FaceRenderPanel { Dock = DockStyle.Right, Width = 250 };

        // Docked right-to-left: stats outermost, then bars, then the face; image fills the rest.
        Controls.Add(_picture);
        Controls.Add(_face);
        Controls.Add(_shapes);
        Controls.Add(_stats);

        BuildPaneMenu();

        // Number keys mirror the context menu; handy while wearing a headset one-handed.
        KeyPreview = true;
        KeyDown += OnPaneShortcut;

        _timer = new System.Windows.Forms.Timer { Interval = ActiveIntervalMs };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>
    /// Right-click anywhere to show or hide panes. Hidden panes are skipped entirely in Tick(),
    /// so turning one off actually reclaims its cost rather than just hiding it.
    /// </summary>
    private void BuildPaneMenu()
    {
        var menu = new ContextMenuStrip();

        AddPaneItem(menu, "&Camera\t1", _picture);
        AddPaneItem(menu, "&Face render\t2", _face);
        AddPaneItem(menu, "&Shape bars\t3", _shapes);
        AddPaneItem(menu, "S&tats\t4", _stats);

        ContextMenuStrip = menu;
        foreach (Control control in Controls) control.ContextMenuStrip = menu;
    }

    private void AddPaneItem(ContextMenuStrip menu, string text, Control pane)
    {
        var item = new ToolStripMenuItem(text) { Checked = pane.Visible, CheckOnClick = true };
        item.CheckedChanged += (_, _) => TogglePane(pane, item.Checked);
        menu.Items.Add(item);

        // Keep the menu honest if a pane is toggled by keyboard instead.
        pane.VisibleChanged += (_, _) => item.Checked = pane.Visible;
    }

    private void TogglePane(Control pane, bool visible)
    {
        pane.Visible = visible;

        // Force a redraw so a pane switched back on fills immediately rather than after
        // the next frame happens to differ.
        if (visible) _lastFrameStamp = -1;
    }

    private void OnPaneShortcut(object? sender, KeyEventArgs e)
    {
        Control? pane = e.KeyCode switch
        {
            Keys.D1 or Keys.NumPad1 => _picture,
            Keys.D2 or Keys.NumPad2 => _face,
            Keys.D3 or Keys.NumPad3 => _shapes,
            Keys.D4 or Keys.NumPad4 => _stats,
            _ => null
        };

        if (pane is null) return;
        TogglePane(pane, !pane.Visible);
        e.Handled = true;
    }

    /// <summary>
    /// Drops to a slow tick while minimised. The window is auto-launched alongside the VRCFT
    /// module and will spend most of its life minimised, so idling cheaply matters more than
    /// it would for a tool you open deliberately.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        bool minimized = WindowState == FormWindowState.Minimized;
        if (minimized == _minimized) return;

        _minimized = minimized;
        _timer.Interval = minimized ? IdleIntervalMs : ActiveIntervalMs;

        // Force a full redraw on restore; the cached frame stamp would otherwise suppress it.
        if (!minimized) _lastFrameStamp = -1;
    }

    private void Tick()
    {
        bool newFrame = false;
        TrackerState state = TrackerState.Stopped;
        double fps = 0, ageMs = -1;
        long frames = 0, recoveries = 0;
        string mode;
        int pid = Environment.ProcessId;
        bool hasWeights = false;

        // Minimised and merely attached: there is nothing to draw and nobody depends on us,
        // so skip the shared-memory read entirely. This is the cheap path.
        if (_minimized && _tracker is null) return;

        if (_tracker is not null)
        {
            mode = "owner (this process)";
            TrackerHealth health = _tracker.Health;
            state = health.State;
            fps = health.Fps;
            frames = health.FramesReceived;
            recoveries = health.Recoveries;
            ageMs = health.MsSinceLastFrame;

            if (_tracker.TryGetLatestFrame(_frame) && _frame.TimestampMs != _lastFrameStamp)
            {
                _lastFrameStamp = _frame.TimestampMs;
                Array.Copy(_frame.Luma, _pixels, _pixels.Length);
                newFrame = true;

                // Inference is rate-limited well below capture: the face render is for eyeballing,
                // and this window should never compete with the tracking itself for CPU.
                long now = Environment.TickCount64;
                bool weightsWanted = _face.Visible || _shapes.Visible;

                if (_inference is not null && weightsWanted && !_minimized
                    && now - _lastInferenceTicks >= 66)
                {
                    _lastInferenceTicks = now;
                    var result = _inference.Run(_frame);
                    for (int i = 0; i < _weights.Length && i < result.Count; i++)
                        _weights[i] = result[i];
                    _hasOwnWeights = true;
                }

                _publisher?.Publish(_frame, health, _hasOwnWeights ? _weights : null);
            }
            else
            {
                _publisher?.PublishHealth(health);
            }

            hasWeights = _hasOwnWeights;
        }
        else if (_subscriber is not null)
        {
            if (!_subscriber.TryConnect())
            {
                UpdateStats(TrackerState.Disconnected, 0, 0, 0, -1,
                    _subscriber.VersionMismatch
                        ? "VERSION MISMATCH - reinstall the VRCFT module"
                        : "waiting for a publisher...", 0);
                return;
            }

            FrameShare.Snapshot? snapshot = _subscriber.TryRead(_pixels, _weights);
            if (snapshot is not null)
            {
                state = snapshot.State;
                fps = snapshot.Fps;
                frames = snapshot.FramesReceived;
                recoveries = snapshot.Recoveries;
                pid = snapshot.PublisherPid;
                hasWeights = snapshot.HasWeights;

                if (snapshot.TimestampMs != _lastFrameStamp)
                {
                    _lastFrameStamp = snapshot.TimestampMs;
                    newFrame = true;
                }
            }
            mode = $"attached to pid {pid}";
        }
        else
        {
            mode = "no source";
        }

        // While minimised we still publish (owner mode, above) but never draw: no pixel
        // conversion, no stats formatting, no invalidation.
        if (_minimized) return;

        // The face render and bars share one weight set; repaint only when it actually moved,
        // and skip the comparison entirely for a hidden pane.
        if (_face.Visible && _face.UpdateWeights(_weights, hasWeights)) _face.Invalidate();
        if (_shapes.Visible && _shapes.Update(_weights, hasWeights)) _shapes.Invalidate();

        // Only touch pixels when there is something new to show, and never when the camera
        // pane is hidden -- the conversion is the single most expensive thing here.
        if (newFrame && _picture.Visible)
        {
            ComputeLumaStats();
            BlitToBitmap();
            _picture.Invalidate();
        }

        UpdateStats(state, fps, frames, recoveries, ageMs, mode, pid);
    }

    /// <summary>Subsampled: min/max/mean over every 16th pixel is visually identical here.</summary>
    private void ComputeLumaStats()
    {
        byte min = 255, max = 0;
        long sum = 0;
        int count = 0;

        for (int i = 0; i < _pixels.Length; i += LumaSampleStride)
        {
            byte b = _pixels[i];
            if (b < min) min = b;
            if (b > max) max = b;
            sum += b;
            count++;
        }

        _min = min;
        _max = max;
        _mean = count > 0 ? (double)sum / count : 0;
    }

    private void BlitToBitmap()
    {
        var rect = new Rectangle(0, 0, _bitmap.Width, _bitmap.Height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < LipFrame.NativeHeight; y++)
            {
                IntPtr destRow = data.Scan0 + y * data.Stride;
                int srcRow = y * LipFrame.NativeWidth;

                // Build the row once, then a single bulk copy. The original did one
                // Marshal.WriteByte per pixel: 160k interop calls per frame.
                if (_showLeftViewOnly)
                {
                    for (int x = 0; x < LipFrame.NativeWidth; x++)
                        _rowBuffer[x] = _greyToArgb[_pixels[srcRow + _leftViewColumns[x]]];
                }
                else
                {
                    for (int x = 0; x < LipFrame.NativeWidth; x++)
                        _rowBuffer[x] = _greyToArgb[_pixels[srcRow + x]];
                }

                Marshal.Copy(_rowBuffer, 0, destRow, LipFrame.NativeWidth);
            }
        }
        finally { _bitmap.UnlockBits(data); }
    }

    private void UpdateStats(TrackerState state, double fps, long frames, long recoveries,
        double ageMs, string mode, int pid)
    {
        long now = Environment.TickCount64;
        if (!_stats.Visible) return;
        if (now - _lastStatsTicks < StatsIntervalMs) return;
        _lastStatsTicks = now;

        TimeSpan uptime = DateTime.UtcNow - _startedUtc;
        string text =
            $"""
             SOURCE
               mode        {mode}
               device      VIVE Facial Tracker
               usb id      VID_0BB4 & PID_0321
               format      400x400 YUY2 @60
               view        {(_showLeftViewOnly ? "left eye (click to toggle)" : "stereo pair (click to toggle)")}

             PIPELINE
               state       {state}
               fps         {fps:0.0}
               frames      {frames:N0}
               recoveries  {recoveries}
               frame age   {(ageMs < 0 ? "n/a" : $"{ageMs:0} ms")}
               uptime      {uptime:hh\:mm\:ss}

             IMAGE
               luma min    {_min}
               luma max    {_max}
               luma mean   {_mean:0.0}
             """;

        // Assigning Text forces a full label relayout, so skip identical updates.
        if (text == _lastStatsText) return;
        _lastStatsText = text;
        _stats.Text = text;

        _stats.ForeColor = state switch
        {
            TrackerState.Streaming => Color.FromArgb(150, 230, 160),
            TrackerState.Starting => Color.FromArgb(230, 220, 150),
            TrackerState.Stalled => Color.FromArgb(240, 180, 120),
            TrackerState.Disconnected or TrackerState.Faulted => Color.FromArgb(240, 140, 140),
            _ => Color.Gainsboro
        };
    }

    /// <summary>
    /// Draws the frame scaled with nearest-neighbour interpolation. PictureBox's Zoom mode uses
    /// GDI+'s smoothing default, which is both slower and wrong for an IR debug view -- seeing
    /// the real sensor pixels is the point.
    /// </summary>
    private sealed class ImagePanel : Panel
    {
        private readonly Bitmap _source;

        public ImagePanel(Bitmap source)
        {
            _source = source;
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The image covers the letterboxed area; only the bars need clearing.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle target = FitPreservingAspect(ClientRectangle, _source.Width, _source.Height);

            using (var brush = new SolidBrush(BackColor))
            {
                foreach (Rectangle bar in Letterbox(ClientRectangle, target))
                    e.Graphics.FillRectangle(brush, bar);
            }

            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            e.Graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            e.Graphics.DrawImage(_source, target);
        }

        private static Rectangle FitPreservingAspect(Rectangle bounds, int width, int height)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return Rectangle.Empty;

            double scale = Math.Min((double)bounds.Width / width, (double)bounds.Height / height);
            int w = Math.Max(1, (int)(width * scale));
            int h = Math.Max(1, (int)(height * scale));
            return new Rectangle(bounds.X + (bounds.Width - w) / 2,
                                 bounds.Y + (bounds.Height - h) / 2, w, h);
        }

        private static IEnumerable<Rectangle> Letterbox(Rectangle bounds, Rectangle target)
        {
            if (target.Top > bounds.Top)
                yield return new Rectangle(bounds.X, bounds.Y, bounds.Width, target.Top - bounds.Top);
            if (target.Bottom < bounds.Bottom)
                yield return new Rectangle(bounds.X, target.Bottom, bounds.Width, bounds.Bottom - target.Bottom);
            if (target.Left > bounds.Left)
                yield return new Rectangle(bounds.X, target.Y, target.Left - bounds.Left, target.Height);
            if (target.Right < bounds.Right)
                yield return new Rectangle(target.Right, target.Y, bounds.Right - target.Right, target.Height);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _tracker?.Dispose();
        _inference?.Dispose();
        _publisher?.Dispose();
        _subscriber?.Dispose();
        _bitmap.Dispose();
        base.OnFormClosed(e);
    }
}
