using System.Drawing.Drawing2D;
using WyvernCenter.Core.Features;
using WyvernCenter.Core.Monitoring;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// Live readings: where the watts go (power draw), then a graph per metric for the last two minutes with its current,
/// min and max. Opens beside the main window at its height; the graphs fill it.
/// </summary>
public sealed class MonitorForm : Form, IFitsHeight
{
    private static readonly (string Key, string Title, string Unit, string Format, Color Color, double? FixedMax)[] Tiles =
    {
        // Two rows of CPU readings, then two of GPU (three per row), then system.
        ("cpuTemp", "CPU temperature", "°C", "F0", Theme.Cpu, 100),
        ("cpuW", "CPU power", "W", "F1", Theme.Cpu, null),
        ("cpuMhz", "CPU clock", "MHz", "F0", Theme.Cpu, null),
        ("cpuLoad", "CPU load", "%", "F0", Theme.Cpu, 100),
        ("cpuFan", "CPU fan", "%", "F0", Theme.Cpu, 100),
        ("ramGb", "RAM in use", "GB", "F1", Theme.Gray, null),
        ("gpuTemp", "GPU temperature", "°C", "F0", Theme.Gpu, 100),
        ("gpuW", "GPU power", "W", "F1", Theme.Gpu, null),
        ("gpuMhz", "GPU clock", "MHz", "F0", Theme.Gpu, null),
        ("gpuLoad", "GPU load", "%", "F0", Theme.Gpu, 100),
        ("gpuFan", "GPU fan", "%", "F0", Theme.Gpu, 100),
        ("batteryW", "Battery (+charge / −drain)", "W", "F1", Theme.Accent, null),
    };
    private const int Cols = 3;

    private const string PowerInfo =
        "Measured: CPU, RAM and the NVIDIA GPU (while it's awake), and on battery the whole laptop (the battery's drain). " +
        "Estimated (~): screen from its brightness, keyboard backlight from its level, fans from their speed. " +
        "On the charger there's no sensor for the total, so it's an estimate.";

    private const string GraphsInfo =
        "The last two minutes. GPU readings appear while the NVIDIA GPU is busy; when it idles Wyvern Center stops asking it, so it can sleep.";

    private readonly AppController app;
    private bool viewing;
    private readonly List<Tile> tiles = new();
    private readonly Label lblPowerTotal;
    private readonly PowerBar powerBar;
    private readonly int tilesTop, pad, gap, tileW;

    public MonitorForm(AppController app)
    {
        this.app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.FormBack;
        ForeColor = Theme.ForeMain;
        Font = Theme.UiFont(10.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Monitor";
        Icon = app.AppIcon;

        var L = new UiLayout(this, 920);
        pad = L.Pad;
        gap = L.Gap;
        tileW = (L.Width - 2 * pad - gap) / Cols;
        L.Space(12);
        (_, lblPowerTotal, _) = L.Header(Glyphs.Bolt, "Power draw", PowerInfo);
        lblPowerTotal.ForeColor = Theme.ForeMain;
        powerBar = L.Add(new PowerBar(), 56);
        L.Space(4);
        L.Header(Glyphs.Clock, "Live graphs", GraphsInfo);
        tilesTop = L.Y;
        for (var i = 0; i < Tiles.Length; i++)
        {
            var t = Tiles[i];
            var tile = new Tile(t.Title, t.Unit, t.Format, t.Color, t.FixedMax) { Tag = t.Key };
            tiles.Add(tile);
            Controls.Add(tile);
        }
        LayTiles(Theme.S(150));
        ResumeLayout(false);

        app.SampleArrived += UpdateAll;
        app.StateChanged += UpdateTitle;
        UpdateTitle();
        // Closing a form never raises VisibleChanged(false): Dispose ends the viewing too.
        VisibleChanged += (_, _) =>
        {
            if (Visible && !viewing) { viewing = true; app.AddViewer(); }
            else if (!Visible && viewing) { viewing = false; app.RemoveViewer(); }
        };
        UpdateAll();
    }

    private void LayTiles(int tileH)
    {
        for (var i = 0; i < tiles.Count; i++)
            tiles[i].Bounds = new Rectangle(pad + (i % Cols) * (tileW + gap), tilesTop + (i / Cols) * (tileH + gap), tileW, tileH);
        var rows = (tiles.Count + Cols - 1) / Cols;
        ClientSize = new Size(pad * 2 + Cols * tileW + gap, tilesTop + rows * (tileH + gap) - gap + pad);
    }

    /// <summary>Graphs share the height left under the power bar (never smaller than readable).</summary>
    public void FitHeight(int height)
    {
        var client = height - (Height - ClientSize.Height);
        var rows = (tiles.Count + Cols - 1) / Cols;
        LayTiles(Math.Max(Theme.S(110), (client - tilesTop - pad - (rows - 1) * gap) / rows));
    }

    private void UpdateTitle()
    {
        if (IsDisposed) return;
        Text = "Monitor — " + (app.ActiveProfile?.Name ?? "no profile picked");
        Icon = app.AppIcon;
    }

    private void UpdateAll()
    {
        if (IsDisposed) return;
        foreach (var t in tiles) t.Bind(app.History[(string)t.Tag!]);
        if (app.Power is { } power)
        {
            powerBar.Total = power;
            lblPowerTotal.Text = PowerBreakdown.TotalText(power);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (viewing) { viewing = false; app.RemoveViewer(); }
            app.SampleArrived -= UpdateAll;
            app.StateChanged -= UpdateTitle;
        }
        base.Dispose(disposing);
    }

    /// <summary>One metric: title, big current value, min/max, and a graph of the history with a scale.</summary>
    private sealed class Tile : Control
    {
        private readonly string title, unit, format;
        private readonly Color color;
        private readonly double? fixedMax;
        private double?[] values = Array.Empty<double?>();
        private double? min, max, last;
        private int capacity = AppController.HistorySeconds;

        public Tile(string title, string unit, string format, Color color, double? fixedMax)
        {
            this.title = title;
            this.unit = unit;
            this.format = format;
            this.color = color;
            this.fixedMax = fixedMax;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.ButtonSecond;
        }

        public void Bind(MetricHistory h)
        {
            values = h.Values;
            capacity = h.Capacity;
            min = h.Min;
            max = h.Max;
            last = h.Last;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var px = Theme.Px(this);
            using var titleFont = Theme.UiFont(10f);
            using var bigFont = new Font("Segoe UI Semibold", 14.5f * Theme.UserScale);
            using var smallFont = Theme.UiFont(9.25f);
            using var dim = new SolidBrush(Color.FromArgb(190, 190, 196)); // light grey: readable on the dark tiles
            using var fore = new SolidBrush(Theme.ForeMain);

            // Top row: title at the left, the current value big at the right. The graph fills the rest.
            g.DrawString(title, titleFont, dim, 10 * px, 10 * px);
            var current = last is double v ? (Math.Abs(v) < 0.05 ? 0 : v).ToString(format) + " " + unit : "—";
            var cs = g.MeasureString(current, bigFont);
            g.DrawString(current, bigFont, fore, Width - cs.Width - 8 * px, 2 * px);

            var plotTop = Math.Max(cs.Height + 6 * px, 36 * px);
            var plot = new RectangleF(10 * px, plotTop, Width - 20 * px, Math.Max(10 * px, Height - plotTop - 22 * px));
            var top = fixedMax ?? NiceTop(Math.Max(1, (max ?? 1) * 1.1));
            using (var grid = new Pen(Color.FromArgb(40, Theme.ForeDim), 1f))
                for (var q = 0; q <= 4; q++)
                {
                    var y = plot.Bottom - plot.Height * q / 4f;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            // Under the graph: its scale at the left, min / max of the last two minutes at the right.
            var scaleText = "0 – " + top.ToString("0.#") + " " + unit;
            g.DrawString(scaleText, smallFont, dim, plot.Left, plot.Bottom + 3 * px);
            var range = min is double mn && max is double mx ? $"min {mn.ToString(format)} · max {mx.ToString(format)}" : "";
            var rs = g.MeasureString(range, smallFont);
            g.DrawString(range, smallFont, dim, plot.Right - rs.Width, plot.Bottom + 3 * px);

            var pts = new List<PointF>();
            using var line = new Pen(color, 2f * px);
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is not double val)
                {
                    if (pts.Count > 1) g.DrawLines(line, pts.ToArray());
                    pts.Clear();
                    continue;
                }
                var x = plot.Left + plot.Width * (capacity - values.Length + i) / Math.Max(1, capacity - 1);
                var y = plot.Bottom - plot.Height * (float)Math.Clamp(val / top, 0, 1);
                pts.Add(new PointF(x, y));
            }
            if (pts.Count > 1) g.DrawLines(line, pts.ToArray());
            else if (pts.Count == 1)
                using (var b = new SolidBrush(color)) g.FillEllipse(b, pts[0].X - 2 * px, pts[0].Y - 2 * px, 4 * px, 4 * px);
        }

        /// <summary>A round scale top (1, 2, 2.5, 5 × 10ⁿ) so the grid lines land on readable values.</summary>
        private static double NiceTop(double v)
        {
            var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
                if (m * mag >= v) return m * mag;
            return 10 * mag;
        }
    }
}
