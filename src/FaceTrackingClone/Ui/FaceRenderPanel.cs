using System.Drawing.Drawing2D;
using FaceTrackingClone.Inference;

namespace FaceTrackingClone.Ui;

/// <summary>
/// Draws a stylised face deformed live by the blendshape weights.
///
/// This is a 2D vector rig rather than a mesh: every feature is a curve whose control points are
/// displaced by the shapes that would drive the equivalent blendshape on an avatar. That is
/// enough to answer the question it exists for -- "is the tracking doing something sensible right
/// now" -- at a glance, without loading VRChat.
///
/// Only the lower face moves, because that is all this hardware tracks. The eyes are drawn
/// deliberately inert and greyed so they are never mistaken for eye tracking.
/// </summary>
internal sealed class FaceRenderPanel : Panel
{
    private readonly float[] _weights = new float[Ipc.FrameShare.WeightCount];
    private bool _hasData;

    private readonly Font _font = new(FontFamily.GenericSansSerif, 7f);
    private readonly SolidBrush _hintBrush = new(Color.FromArgb(120, 120, 128));
    private readonly SolidBrush _skinBrush = new(Color.FromArgb(58, 58, 68));
    private readonly SolidBrush _cheekBrush = new(Color.FromArgb(74, 74, 88));
    private readonly SolidBrush _mouthBrush = new(Color.FromArgb(16, 12, 16));
    private readonly SolidBrush _tongueBrush = new(Color.FromArgb(184, 96, 112));
    private readonly SolidBrush _teethBrush = new(Color.FromArgb(222, 222, 228));
    private readonly Pen _outlinePen = new(Color.FromArgb(142, 142, 156), 1.6f);
    private readonly Pen _lipPen = new(Color.FromArgb(208, 150, 162), 2f);
    private readonly Pen _inertPen = new(Color.FromArgb(70, 70, 78), 1.4f);

    public FaceRenderPanel()
    {
        BackColor = Color.FromArgb(18, 18, 22);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Copies in new weights; returns true when the drawing would visibly change.</summary>
    public bool UpdateWeights(float[] weights, bool hasData)
    {
        bool changed = _hasData != hasData;
        _hasData = hasData;

        int count = Math.Min(_weights.Length, weights.Length);
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(_weights[i] - weights[i]) > 0.004f) changed = true;
            _weights[i] = weights[i];
        }
        return changed;
    }

    private float W(BabbleShape shape)
    {
        int i = (int)shape;
        return i >= 0 && i < _weights.Length ? Math.Clamp(_weights[i], 0f, 1f) : 0f;
    }

    /// <summary>Jaw opening, suppressed by mouthClose (lips deliberately held together).</summary>
    private float JawOpenAmount() =>
        Math.Clamp(W(BabbleShape.JawOpen) * (1f - 0.85f * W(BabbleShape.MouthClose)), 0f, 1f);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        if (!_hasData)
        {
            g.DrawString("no blendshape data", _font, _hintBrush, 10, 10);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = Math.Min(Width, Height) * 0.80f;
        float cx = Width / 2f;
        float cy = Height / 2f;

        DrawHead(g, cx, cy, s);
        DrawInertEyes(g, cx, cy, s);
        DrawNose(g, cx, cy, s);
        DrawCheeks(g, cx, cy, s);
        DrawMouth(g, cx, cy, s);

        g.DrawString("lower face only - eyes not tracked", _font, _hintBrush, 6, Height - 16);
    }

    private void DrawHead(Graphics g, float cx, float cy, float s)
    {
        // The chin drops with the jaw, so the silhouette lengthens as the mouth opens.
        float jaw = JawOpenAmount();
        float halfW = s * 0.34f;
        float top = cy - s * 0.46f;
        float bottom = cy + s * 0.44f + jaw * s * 0.10f;
        float sway = (W(BabbleShape.JawRight) - W(BabbleShape.JawLeft)) * s * 0.05f;

        using var path = new GraphicsPath();
        path.AddBezier(cx - halfW, cy - s * 0.05f, cx - halfW, top,
                       cx + halfW, top, cx + halfW, cy - s * 0.05f);
        path.AddBezier(cx + halfW, cy - s * 0.05f,
                       cx + halfW * 0.95f + sway, cy + s * 0.30f,
                       cx + halfW * 0.45f + sway, bottom,
                       cx + sway, bottom);
        path.AddBezier(cx + sway, bottom,
                       cx - halfW * 0.45f + sway, bottom,
                       cx - halfW * 0.95f + sway, cy + s * 0.30f,
                       cx - halfW, cy - s * 0.05f);

        g.FillPath(_skinBrush, path);
        g.DrawPath(_outlinePen, path);
    }

    private void DrawInertEyes(Graphics g, float cx, float cy, float s)
    {
        float eyeY = cy - s * 0.16f;
        float dx = s * 0.15f;
        float w = s * 0.10f, h = s * 0.035f;

        foreach (float x in new[] { cx - dx, cx + dx })
            g.DrawArc(_inertPen, x - w / 2, eyeY - h / 2, w, h, 200, 140);
    }

    private void DrawNose(Graphics g, float cx, float cy, float s)
    {
        float sneer = (W(BabbleShape.NoseSneerLeft) + W(BabbleShape.NoseSneerRight)) * 0.5f;
        float noseBottom = cy + s * 0.04f - sneer * s * 0.03f;

        using var path = new GraphicsPath();
        path.AddLine(cx, cy - s * 0.08f, cx - s * 0.045f, noseBottom);
        path.AddBezier(cx - s * 0.045f, noseBottom,
                       cx - s * 0.02f, noseBottom + s * 0.02f,
                       cx + s * 0.02f, noseBottom + s * 0.02f,
                       cx + s * 0.045f, noseBottom);
        g.DrawPath(_inertPen, path);
    }

    private void DrawCheeks(Graphics g, float cx, float cy, float s)
    {
        float left = W(BabbleShape.CheekPuffLeft) - W(BabbleShape.CheekSuckLeft) * 0.6f;
        float right = W(BabbleShape.CheekPuffRight) - W(BabbleShape.CheekSuckRight) * 0.6f;

        DrawCheek(g, cx - s * 0.22f, cy + s * 0.10f, s, left);
        DrawCheek(g, cx + s * 0.22f, cy + s * 0.10f, s, right);
    }

    private void DrawCheek(Graphics g, float x, float y, float s, float amount)
    {
        if (Math.Abs(amount) < 0.02f) return;

        float r = s * (0.06f + 0.07f * Math.Abs(amount));
        var rect = new RectangleF(x - r, y - r * 0.75f, r * 2, r * 1.5f);

        if (amount > 0) g.FillEllipse(_cheekBrush, rect);
        g.DrawEllipse(_inertPen, rect);
    }

    private void DrawMouth(Graphics g, float cx, float cy, float s)
    {
        float open = JawOpenAmount();
        float pucker = W(BabbleShape.MouthPucker);
        float funnel = W(BabbleShape.MouthFunnel);
        float stretch = (W(BabbleShape.MouthStretchLeft) + W(BabbleShape.MouthStretchRight)) * 0.5f;

        // Pucker narrows the mouth, stretch widens it.
        float halfW = s * 0.16f * (1f - 0.45f * pucker - 0.20f * funnel + 0.30f * stretch);
        halfW = Math.Max(halfW, s * 0.05f);

        float shift = (W(BabbleShape.MouthRight) - W(BabbleShape.MouthLeft)) * s * 0.06f
                    + (W(BabbleShape.JawRight) - W(BabbleShape.JawLeft)) * s * 0.03f;

        float mouthY = cy + s * 0.20f;

        float cornerL = -W(BabbleShape.MouthSmileLeft) * s * 0.06f
                        + W(BabbleShape.MouthFrownLeft) * s * 0.05f;
        float cornerR = -W(BabbleShape.MouthSmileRight) * s * 0.06f
                        + W(BabbleShape.MouthFrownRight) * s * 0.05f;

        float lx = cx - halfW + shift, ly = mouthY + cornerL;
        float rx = cx + halfW + shift, ry = mouthY + cornerR;

        // The jaw contributes far more opening than the upper lip does.
        float upperLift = open * s * 0.055f
            + (W(BabbleShape.MouthUpperUpLeft) + W(BabbleShape.MouthUpperUpRight)) * 0.5f * s * 0.03f;
        float lowerDrop = open * s * 0.20f
            + (W(BabbleShape.MouthLowerDownLeft) + W(BabbleShape.MouthLowerDownRight)) * 0.5f * s * 0.03f;

        float round = 1f + funnel * 0.8f + pucker * 0.5f;
        float upperY = mouthY - upperLift * round;
        float lowerY = mouthY + lowerDrop * round;

        // Lip suck flattens the curves.
        float rollUpper = W(BabbleShape.MouthRollUpper);
        float rollLower = W(BabbleShape.MouthRollLower);

        using var cavity = new GraphicsPath();
        cavity.AddBezier(lx, ly,
            cx - halfW * 0.5f + shift, upperY - (1f - rollUpper) * s * 0.012f,
            cx + halfW * 0.5f + shift, upperY - (1f - rollUpper) * s * 0.012f,
            rx, ry);
        cavity.AddBezier(rx, ry,
            cx + halfW * 0.5f + shift, lowerY + (1f - rollLower) * s * 0.02f,
            cx - halfW * 0.5f + shift, lowerY + (1f - rollLower) * s * 0.02f,
            lx, ly);
        cavity.CloseFigure();

        g.FillPath(_mouthBrush, cavity);

        if (open > 0.25f)
        {
            using var teeth = new GraphicsPath();
            float teethDepth = Math.Min(s * 0.022f, (lowerY - upperY) * 0.28f);
            teeth.AddBezier(lx, ly,
                cx - halfW * 0.5f + shift, upperY,
                cx + halfW * 0.5f + shift, upperY, rx, ry);
            teeth.AddBezier(rx, ry,
                cx + halfW * 0.5f + shift, upperY + teethDepth,
                cx - halfW * 0.5f + shift, upperY + teethDepth, lx, ly);
            teeth.CloseFigure();

            var clip = g.Clip;
            g.SetClip(cavity);
            g.FillPath(_teethBrush, teeth);
            g.Clip = clip;
        }

        DrawTongue(g, cx + shift, mouthY, s, cavity, open);

        g.DrawPath(_lipPen, cavity);
    }

    private void DrawTongue(Graphics g, float cx, float mouthY, float s,
        GraphicsPath cavity, float open)
    {
        float outAmount = W(BabbleShape.TongueOut);
        if (outAmount < 0.05f || open < 0.1f) return;

        float tx = cx + (W(BabbleShape.TongueRight) - W(BabbleShape.TongueLeft)) * s * 0.04f;
        float ty = mouthY + s * 0.05f
                 + (W(BabbleShape.TongueDown) - W(BabbleShape.TongueUp)) * s * 0.03f;

        float tw = s * 0.055f * (1f + 0.4f * W(BabbleShape.TongueSquish));
        float th = s * 0.045f * (0.6f + 1.1f * outAmount);

        var clip = g.Clip;
        g.SetClip(cavity, CombineMode.Intersect);
        g.FillEllipse(_tongueBrush, tx - tw, ty - th * 0.5f, tw * 2, th * 2);
        g.Clip = clip;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _hintBrush.Dispose();
            _skinBrush.Dispose();
            _cheekBrush.Dispose();
            _mouthBrush.Dispose();
            _tongueBrush.Dispose();
            _teethBrush.Dispose();
            _outlinePen.Dispose();
            _lipPen.Dispose();
            _inertPen.Dispose();
        }
        base.Dispose(disposing);
    }
}
