using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WheelBridge.Gui;

/// <summary>Bipolar bar: fills from the center toward the value; turns red when it hits the range.</summary>
public sealed class CenterBar : Control
{
    private float _value;
    public float Range { get; set; } = 1;
    public string Format { get; set; } = "+0.00;-0.00; 0.00";

    public CenterBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 20;
        Font = new Font("Consolas", 8.5f);
    }

    public float Value
    {
        get => _value;
        set { if (value != _value) { _value = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        g.FillRectangle(SystemBrushes.Window, r);
        g.DrawRectangle(SystemPens.ControlDark, r.X, r.Y, r.Width - 1, r.Height - 1);
        int mid = r.Width / 2;
        float frac = Math.Clamp(_value / Range, -1, 1);
        int w = (int)Math.Round(Math.Abs(frac) * (mid - 2));
        bool clip = Math.Abs(_value) >= Range * 0.999f;
        using (var b = new SolidBrush(clip ? Color.IndianRed : _value >= 0 ? Color.SteelBlue : Color.DarkOrange))
            g.FillRectangle(b, frac >= 0 ? mid : mid - w, 3, w, r.Height - 6);
        g.DrawLine(SystemPens.ControlText, mid, 1, mid, r.Height - 2);
        TextRenderer.DrawText(g, _value.ToString(Format), Font, new Rectangle(4, 0, r.Width - 8, r.Height),
            SystemColors.GrayText, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}

/// <summary>Flicker-free monospace text block, for the telemetry monitor.</summary>
public sealed class MonitorView : Control
{
    private string _text = "";

    public MonitorView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font("Consolas", 9.5f);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(214, 220, 228);
    }

    public string Content
    {
        get => _text;
        set { if (value != _text) { _text = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, _text, Font, new Point(8, 8), ForeColor, TextFormatFlags.NoPrefix | TextFormatFlags.ExpandTabs);
    }
}

/// <summary>Plots the wheel-angle → stick mapping (lock, linearity, anti-deadzone) with the live wheel position.</summary>
public sealed class SteeringCurve : Control
{
    private readonly BridgeConfig _cfg;
    private readonly Func<float?> _angle;

    public SteeringCurve(BridgeConfig cfg, Func<float?> angle)
    {
        _cfg = cfg;
        _angle = angle;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 8f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SystemColors.Window);
        var r = Rectangle.Inflate(ClientRectangle, -10, -10);
        if (r.Width < 20 || r.Height < 20) return;
        float half = Math.Max(10, _cfg.SteeringLockDegrees / 2);
        float span = half * 1.15f;   // show a little past lock
        PointF Map(float deg, float stick) =>
            new(r.Left + (deg / span + 1) / 2 * r.Width, r.Top + (1 - stick) / 2 * r.Height);

        g.DrawLine(SystemPens.ControlLight, Map(-span, 0), Map(span, 0));
        g.DrawLine(SystemPens.ControlLight, Map(0, -1), Map(0, 1));
        using (var dash = new Pen(Color.Silver) { DashStyle = DashStyle.Dash })
        {
            g.DrawLine(dash, Map(-half, -1), Map(-half, 1));
            g.DrawLine(dash, Map(half, -1), Map(half, 1));
        }
        var pts = new List<PointF>();
        for (int i = 0; i <= 200; i++)
        {
            float deg = -span + 2 * span * i / 200;
            pts.Add(Map(deg, VirtualPad.SteeringToStick(deg, _cfg)));
        }
        using (var pen = new Pen(Color.SteelBlue, 2)) g.DrawLines(pen, pts.ToArray());
        if (_angle() is { } a)
        {
            var p = Map(Math.Clamp(a, -span, span), VirtualPad.SteeringToStick(a, _cfg));
            g.FillEllipse(Brushes.OrangeRed, p.X - 5, p.Y - 5, 10, 10);
        }
        TextRenderer.DrawText(g, $"wheel ±{half:F0}° → stick ±1", Font, new Point(r.Left, r.Top), SystemColors.GrayText);
    }
}

/// <summary>Minimal text prompt (WinForms has none built in).</summary>
public static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string label, string initial = "")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(360, 110), AutoScaleMode = AutoScaleMode.Dpi,
        };
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 336 };
        var box = new TextBox { Text = initial, Left = 12, Top = 36, Width = 336 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Top = 72, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 273, Top = 72, Width = 75 };
        f.Controls.AddRange([lbl, box, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
