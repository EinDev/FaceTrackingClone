using FaceTrackingClone.Inference;

namespace FaceTrackingClone.Ui;

/// <summary>
/// Live bar display of the 45 blendshape weights the model produces.
///
/// These are the model outputs, i.e. the source of truth for this pipeline, before they are
/// fanned out to VRCFaceTracking's finer-grained Unified Expressions set. Showing them raw is
/// what makes it possible to tell "the model is not detecting my jaw" apart from "the mapping
/// to Unified is wrong", which are otherwise indistinguishable from inside VRChat.
/// </summary>
internal sealed class ShapePanel : Panel
{
    private const int RowHeight = 13;
    private const int Columns = 2;
    private const float ActiveThreshold = 0.15f;

    private readonly float[] _weights = new float[FaceTrackingClone.Ipc.FrameShare.WeightCount];
    private readonly Font _font = new(FontFamily.GenericMonospace, 6.5f);

    private readonly SolidBrush _labelBrush = new(Color.FromArgb(150, 150, 158));
    private readonly SolidBrush _activeLabelBrush = new(Color.FromArgb(225, 225, 230));
    private readonly SolidBrush _trackBrush = new(Color.FromArgb(38, 38, 44));
    private readonly SolidBrush _barBrush = new(Color.FromArgb(90, 170, 230));
    private readonly SolidBrush _activeBarBrush = new(Color.FromArgb(120, 220, 160));

    private bool _hasData;

    public ShapePanel()
    {
        BackColor = Color.FromArgb(18, 18, 22);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Copies in new weights. Returns true when the display actually changed.</summary>
    public bool Update(float[] weights, bool hasData)
    {
        bool changed = _hasData != hasData;
        _hasData = hasData;

        int count = Math.Min(_weights.Length, weights.Length);
        for (int i = 0; i < count; i++)
        {
            // Quantised comparison: sub-1% wiggle is invisible and would force constant repaints.
            if (Math.Abs(_weights[i] - weights[i]) > 0.005f) changed = true;
            _weights[i] = weights[i];
        }

        return changed;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        if (!_hasData)
        {
            g.DrawString("no blendshape data\n(publisher has no model loaded)",
                _font, _labelBrush, 8, 8);
            return;
        }

        int rows = (int)Math.Ceiling(_weights.Length / (double)Columns);
        int columnWidth = Math.Max(120, (Width - 12) / Columns);

        for (int i = 0; i < _weights.Length; i++)
        {
            int column = i / rows;
            int row = i % rows;

            int x = 6 + column * columnWidth;
            int y = 4 + row * RowHeight;
            if (y + RowHeight > Height) continue;

            float weight = Math.Clamp(_weights[i], 0f, 1f);
            bool active = weight >= ActiveThreshold;

            // Name
            g.DrawString(BabbleShapes.NameOf(i), _font,
                active ? _activeLabelBrush : _labelBrush, x, y - 1);

            // Bar, right-aligned within the column
            int barWidth = 54;
            int barX = x + columnWidth - barWidth - 10;
            var track = new Rectangle(barX, y + 2, barWidth, RowHeight - 6);
            g.FillRectangle(_trackBrush, track);

            int filled = (int)(barWidth * weight);
            if (filled > 0)
            {
                g.FillRectangle(active ? _activeBarBrush : _barBrush,
                    new Rectangle(track.X, track.Y, filled, track.Height));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _labelBrush.Dispose();
            _activeLabelBrush.Dispose();
            _trackBrush.Dispose();
            _barBrush.Dispose();
            _activeBarBrush.Dispose();
        }
        base.Dispose(disposing);
    }
}
