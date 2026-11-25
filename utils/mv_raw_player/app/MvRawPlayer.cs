using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MvRawPlayer
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// Finds the HAL plugin folder shipped next to the application (…\lib\metavision\hal\plugins)
        /// and exports it through MV_HAL_PLUGIN_PATH so that RAW files can be decoded.
        /// </summary>
        private static void ConfigurePluginPath()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MV_HAL_PLUGIN_PATH")))
                return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string[] candidates =
            {
                Path.Combine(baseDir, "lib", "metavision", "hal", "plugins"),
                Path.Combine(baseDir, "..", "lib", "metavision", "hal", "plugins"),
                Path.Combine(baseDir, "plugins"),
                Path.Combine(baseDir, "..", "plugins"),
            };
            foreach (string c in candidates)
            {
                if (Directory.Exists(c))
                {
                    Environment.SetEnvironmentVariable("MV_HAL_PLUGIN_PATH", Path.GetFullPath(c));
                    return;
                }
            }
            // last resort: look for the plugin DLL anywhere under the application folder or its parent
            foreach (string root in new[] { baseDir, Path.GetFullPath(Path.Combine(baseDir, "..")) })
            {
                try
                {
                    string[] hits = Directory.GetFiles(root, "*hal_plugin*.dll", SearchOption.AllDirectories);
                    if (hits.Length > 0)
                    {
                        Environment.SetEnvironmentVariable("MV_HAL_PLUGIN_PATH", Path.GetDirectoryName(hits[0]));
                        return;
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            ConfigurePluginPath();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }

    /// <summary>P/Invoke bindings for mv_player.dll (C++ wrapper around the Metavision SDK).</summary>
    internal static class Native
    {
        private const string Dll = "mv_player";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mvp_last_error();

        public static string LastError()
        {
            IntPtr p = mvp_last_error();
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mvp_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out int width, out int height, out long startUs, out long endUs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_close(IntPtr h);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_set_playing(IntPtr h, int playing);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_is_playing(IntPtr h);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_set_speed(IntPtr h, double speed);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_set_loop(IntPtr h, int loop);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_set_accumulation_us(IntPtr h, long us);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_is_ended(IntPtr h);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_seek(IntPtr h, long tsUs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern long mvp_get_position_us(IntPtr h);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_render_bgra(IntPtr h, IntPtr dst, int stride);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_an_config(IntPtr h, int maxHz, int windowMs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_roi_set(IntPtr h, int idx, int x0, int y0, int x1, int y1);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mvp_roi_clear_all(IntPtr h);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_roi_hist(IntPtr h, int idx, [In, Out] float[] buf, int n);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_roi_peaks(IntPtr h, int idx, [In, Out] double[] hz, [In, Out] double[] strength,
            int maxN, out double total);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_roi_amp(IntPtr h, int idx, double f0, [In, Out] double[] outv);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mvp_roi_series(IntPtr h, int idx, int axis, int maxN, [In, Out] float[] tMs,
            [In, Out] float[] val, out double windowMs);
    }

    /// <summary>Video surface with mouse-wheel zoom (around the cursor), drag panning and double-click reset.</summary>
    internal sealed class VideoView : Control
    {
        private const double MinZoom = 0.25;
        private const double MaxZoom = 64.0;

        /// <summary>A region of interest shown on top of the video (image coordinates, right/bottom exclusive).</summary>
        internal sealed class RoiOverlay
        {
            public Rectangle R;
            public string Label = "";
            public Color Color = Color.Lime;
            public int Slot;
        }

        public readonly List<RoiOverlay> Rois = new List<RoiOverlay>();
        public event Action<Rectangle> RoiDrawn;
        public event Action<int> RoiRemoveRequested;

        private bool _roiMode;
        private bool _roiDraw;
        private Point _roiA, _roiB;

        public bool RoiMode
        {
            get { return _roiMode; }
            set
            {
                _roiMode = value;
                Cursor = value ? Cursors.Cross : Cursors.Default;
            }
        }

        private Image _image;
        private double _zoom = 1.0;          // 1.0 = fit to window
        private double _panX, _panY;         // offset of the image centre from the view centre (pixels)
        private bool _panning;
        private Point _panStart;
        private double _panStartX, _panStartY;

        public VideoView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public Image Image
        {
            get { return _image; }
            set
            {
                bool first = _image == null || value == null;
                _image = value;
                if (first)
                    ResetView();
                Invalidate();
            }
        }

        public double Zoom { get { return _zoom; } }

        /// <summary>Raised for a mouse wheel notch over the view (used when the form-level filter does not see it).</summary>
        public event Action<int, Point> WheelMoved;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Action<int, Point> h = WheelMoved;
            if (h != null)
                h(e.Delta, e.Location);
        }

        public void ResetView()
        {
            _zoom = 1.0;
            _panX = _panY = 0.0;
            Invalidate();
        }

        private double FitScale()
        {
            if (_image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                return 1.0;
            return Math.Min(ClientSize.Width / (double)_image.Width, ClientSize.Height / (double)_image.Height);
        }

        /// <summary>Zooms by <paramref name="factor"/> keeping the image point under <paramref name="pt"/> fixed.</summary>
        public void ZoomAt(Point pt, double factor)
        {
            if (_image == null)
                return;
            double oldScale = FitScale() * _zoom;
            double newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom * factor));
            double newScale = FitScale() * newZoom;
            double cx = ClientSize.Width / 2.0, cy = ClientSize.Height / 2.0;
            // image coordinate under the cursor, relative to the image centre
            double ix = (pt.X - cx - _panX) / oldScale;
            double iy = (pt.Y - cy - _panY) / oldScale;
            _zoom = newZoom;
            _panX = pt.X - cx - ix * newScale;
            _panY = pt.Y - cy - iy * newScale;
            ClampPan();
            Invalidate();
        }

        private void GetMapping(out double ox, out double oy, out double scale)
        {
            scale = FitScale() * _zoom;
            ox = ClientSize.Width / 2.0 + _panX - _image.Width * scale / 2.0;
            oy = ClientSize.Height / 2.0 + _panY - _image.Height * scale / 2.0;
        }

        private Point ViewToImage(Point pt)
        {
            double ox, oy, sc;
            GetMapping(out ox, out oy, out sc);
            int x = (int)Math.Round((pt.X - ox) / sc);
            int y = (int)Math.Round((pt.Y - oy) / sc);
            return new Point(Math.Max(0, Math.Min(_image.Width, x)), Math.Max(0, Math.Min(_image.Height, y)));
        }

        private PointF ImageToView(double x, double y)
        {
            double ox, oy, sc;
            GetMapping(out ox, out oy, out sc);
            return new PointF((float)(ox + x * sc), (float)(oy + y * sc));
        }

        private void ClampPan()
        {
            if (_image == null)
                return;
            double scale = FitScale() * _zoom;
            double hw = _image.Width * scale / 2.0, hh = _image.Height * scale / 2.0;
            double lx = ClientSize.Width / 2.0 + hw - 40, ly = ClientSize.Height / 2.0 + hh - 40;
            _panX = Math.Max(-lx, Math.Min(lx, _panX));
            _panY = Math.Max(-ly, Math.Min(ly, _panY));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_image == null)
                return;
            if (e.Button == MouseButtons.Right)
            {
                Point ip = ViewToImage(e.Location);
                for (int i = Rois.Count - 1; i >= 0; --i)
                {
                    if (Rois[i].R.Contains(ip))
                    {
                        Action<int> h = RoiRemoveRequested;
                        if (h != null)
                            h(Rois[i].Slot);
                        break;
                    }
                }
                return;
            }
            if (e.Button == MouseButtons.Left && (_roiMode || (ModifierKeys & Keys.Shift) != 0))
            {
                _roiDraw = true;
                _roiA = _roiB = ViewToImage(e.Location);
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                _panning = true;
                _panStart = e.Location;
                _panStartX = _panX;
                _panStartY = _panY;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_roiDraw)
            {
                _roiB = ViewToImage(e.Location);
                Invalidate();
                return;
            }
            if (_panning)
            {
                _panX = _panStartX + (e.X - _panStart.X);
                _panY = _panStartY + (e.Y - _panStart.Y);
                ClampPan();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_roiDraw)
            {
                _roiDraw = false;
                var r = new Rectangle(Math.Min(_roiA.X, _roiB.X), Math.Min(_roiA.Y, _roiB.Y),
                    Math.Abs(_roiB.X - _roiA.X), Math.Abs(_roiB.Y - _roiA.Y));
                Invalidate();
                Action<Rectangle> h = RoiDrawn;
                if (h != null && r.Width >= 4 && r.Height >= 4)
                    h(r);
                return;
            }
            _panning = false;
            Cursor = _roiMode ? Cursors.Cross : Cursors.Default;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            ResetView();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampPan();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (_image == null)
                return;
            double scale = FitScale() * _zoom;
            double w = _image.Width * scale, h = _image.Height * scale;
            double x = ClientSize.Width / 2.0 + _panX - w / 2.0;
            double y = ClientSize.Height / 2.0 + _panY - h / 2.0;
            g.InterpolationMode = scale >= 1.0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_image, new RectangleF((float)x, (float)y, (float)w, (float)h));

            using (var lf = new Font("Segoe UI", 9f, FontStyle.Bold))
            {
                foreach (RoiOverlay ro in Rois)
                {
                    PointF a = ImageToView(ro.R.Left, ro.R.Top);
                    PointF b = ImageToView(ro.R.Right, ro.R.Bottom);
                    using (var pen = new Pen(ro.Color, 2f))
                        g.DrawRectangle(pen, a.X, a.Y, b.X - a.X, b.Y - a.Y);
                    SizeF ls = g.MeasureString(ro.Label, lf);
                    using (var bb = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                        g.FillRectangle(bb, a.X, a.Y - ls.Height, ls.Width, ls.Height);
                    using (var tb = new SolidBrush(ro.Color))
                        g.DrawString(ro.Label, lf, tb, a.X, a.Y - ls.Height);
                }
            }
            if (_roiDraw)
            {
                PointF a = ImageToView(Math.Min(_roiA.X, _roiB.X), Math.Min(_roiA.Y, _roiB.Y));
                PointF b = ImageToView(Math.Max(_roiA.X, _roiB.X), Math.Max(_roiA.Y, _roiB.Y));
                using (var pen = new Pen(Color.White, 1f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, a.X, a.Y, b.X - a.X, b.Y - a.Y);
            }

            string label = string.Format("{0:0.##}x  ({1}%)", _zoom, (int)Math.Round(scale * 100.0));
            using (var f = new Font("Segoe UI", 9f))
            {
                SizeF sz = g.MeasureString(label, f);
                using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.FillRectangle(b, 6, 6, sz.Width + 8, sz.Height + 2);
                g.DrawString(label, f, Brushes.Gainsboro, 10, 7);
            }
        }
    }

    /// <summary>Displacement waveform (X / Y position of the vibrating object, mean removed).</summary>
    internal sealed class WaveView : Control
    {
        private float[] _tx = new float[0], _vx = new float[0], _ty = new float[0], _vy = new float[0];
        private double _winMs = 1000;
        private double _mmPerPx;

        public WaveView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 30);
        }

        public void SetData(float[] tx, float[] vx, int nx, float[] ty, float[] vy, int ny, double winMs, double mmPerPx)
        {
            _tx = new float[Math.Max(0, nx)]; _vx = new float[_tx.Length];
            Array.Copy(tx, _tx, _tx.Length); Array.Copy(vx, _vx, _vx.Length);
            _ty = new float[Math.Max(0, ny)]; _vy = new float[_ty.Length];
            Array.Copy(ty, _ty, _ty.Length); Array.Copy(vy, _vy, _vy.Length);
            _winMs = Math.Max(1.0, winMs);
            _mmPerPx = mmPerPx;
            Invalidate();
        }

        public void Clear()
        {
            _tx = new float[0]; _vx = new float[0]; _ty = new float[0]; _vy = new float[0];
            Invalidate();
        }

        private void DrawTrace(Graphics g, Rectangle plot, float[] t, float[] v, double range, Color col)
        {
            if (t.Length < 2)
                return;
            var pts = new PointF[t.Length];
            for (int i = 0; i < t.Length; ++i)
            {
                float x = plot.Left + (float)(t[i] / _winMs * plot.Width);
                float y = plot.Top + plot.Height / 2f - (float)(v[i] / range * (plot.Height / 2.0 - 2));
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(col, 1.5f))
                g.DrawLines(pen, pts);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            var plot = new Rectangle(46, 22, Math.Max(10, Width - 56), Math.Max(10, Height - 22 - 20));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var f = new Font("Segoe UI", 8.5f))
            using (var fb = new Font("Segoe UI", 9f, FontStyle.Bold))
            {
                g.DrawString("변위 파형 (평균 제거)", fb, Brushes.Gainsboro, 4, 2);
                using (var axis = new Pen(Color.FromArgb(90, 95, 105)))
                    g.DrawRectangle(axis, plot);
                double max = 1.0;
                foreach (float v in _vx) max = Math.Max(max, Math.Abs(v));
                foreach (float v in _vy) max = Math.Max(max, Math.Abs(v));
                double range = 1;
                double[] nice = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };
                foreach (double nv in nice) { range = nv; if (nv >= max * 1.1) break; }
                using (var zp = new Pen(Color.FromArgb(60, 65, 75)))
                    g.DrawLine(zp, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);
                string top = "+" + range.ToString("0") + " px";
                string bot = "-" + range.ToString("0") + " px";
                g.DrawString(top, f, Brushes.Silver, 2, plot.Top - 2);
                g.DrawString(bot, f, Brushes.Silver, 2, plot.Bottom - 14);
                if (_mmPerPx > 0)
                    g.DrawString("(" + (range * _mmPerPx).ToString("0.###") + " mm)", f, Brushes.DimGray, 2, plot.Top + 10);
                DrawTrace(g, plot, _tx, _vx, range, Color.Cyan);
                DrawTrace(g, plot, _ty, _vy, range, Color.Orange);
                g.DrawString("0", f, Brushes.Silver, plot.Left - 4, plot.Bottom + 2);
                string te = _winMs.ToString("0") + " ms";
                SizeF ts = g.MeasureString(te, f);
                g.DrawString(te, f, Brushes.Silver, plot.Right - ts.Width, plot.Bottom + 2);
                g.DrawString("X", fb, Brushes.Cyan, plot.Right - 34, 2);
                g.DrawString("Y", fb, Brushes.Orange, plot.Right - 16, 2);
            }
        }
    }

    /// <summary>Frequency histogram of one ROI with peak markers.</summary>
    internal sealed class SpectrumView : Control
    {
        private float[] _h = new float[0];
        private double[] _pk = new double[0];
        private string _title = "";
        private Color _color = Color.Lime;

        public SpectrumView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 30);
        }

        public void SetData(float[] hist, int n, double[] peaks, string title, Color color)
        {
            _h = new float[Math.Max(0, n)];
            Array.Copy(hist, _h, _h.Length);
            _pk = peaks;
            _title = title;
            _color = color;
            Invalidate();
        }

        public void Clear()
        {
            _h = new float[0];
            _pk = new double[0];
            _title = "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            var plot = new Rectangle(34, 24, Math.Max(10, Width - 44), Math.Max(10, Height - 24 - 24));
            using (var f = new Font("Segoe UI", 8.5f))
            using (var fb = new Font("Segoe UI", 9f, FontStyle.Bold))
            {
                g.DrawString(_title.Length > 0 ? _title : "ROI를 지정하면 주파수 스펙트럼이 표시됩니다", fb,
                    Brushes.Gainsboro, 4, 3);
                using (var axis = new Pen(Color.FromArgb(90, 95, 105)))
                    g.DrawRectangle(axis, plot);
                int n = _h.Length;
                if (n < 3)
                    return;
                int maxHz = n - 1;
                double step = 50;
                while (maxHz / step > 10) step *= 2;
                while (maxHz / step < 4 && step > 10) step /= 2;
                for (double hz = 0; hz <= maxHz + 0.01; hz += step)
                {
                    float x = plot.Left + (float)(hz / maxHz * plot.Width);
                    using (var gp = new Pen(Color.FromArgb(45, 50, 58)))
                        g.DrawLine(gp, x, plot.Top, x, plot.Bottom);
                    string t = hz.ToString("0");
                    SizeF ts = g.MeasureString(t, f);
                    g.DrawString(t, f, Brushes.Silver, x - ts.Width / 2, plot.Bottom + 2);
                }
                g.DrawString("Hz", f, Brushes.Silver, 4, plot.Bottom + 2);

                float max = 1f;
                for (int i = 1; i < n; ++i)
                    if (_h[i] > max) max = _h[i];
                using (var br = new SolidBrush(Color.FromArgb(170, _color)))
                {
                    for (int px = 0; px < plot.Width; ++px)
                    {
                        int b0 = 1 + (int)((long)px * (n - 1) / plot.Width);
                        int b1 = Math.Max(b0, 1 + (int)((long)(px + 1) * (n - 1) / plot.Width) - 1);
                        float v = 0;
                        for (int b = b0; b <= b1 && b < n; ++b)
                            if (_h[b] > v) v = _h[b];
                        int hgt = (int)(v / max * (plot.Height - 4));
                        if (hgt > 0)
                            g.FillRectangle(br, plot.Left + px, plot.Bottom - hgt, 1, hgt);
                    }
                }
                int k = 0;
                foreach (double hz in _pk)
                {
                    float x = plot.Left + (float)(hz / maxHz * plot.Width);
                    using (var pp = new Pen(Color.Yellow, 1f) { DashStyle = DashStyle.Dot })
                        g.DrawLine(pp, x, plot.Top, x, plot.Bottom);
                    string t = hz.ToString("0.0") + " Hz";
                    SizeF ts = g.MeasureString(t, f);
                    float tx = Math.Min(Math.Max(plot.Left, x - ts.Width / 2), plot.Right - ts.Width);
                    g.DrawString(t, f, Brushes.Yellow, tx, plot.Top + 2 + (k % 3) * 13);
                    if (++k >= 3) break;
                }
                g.DrawString("빈도", f, Brushes.Silver, 2, plot.Top);
            }
        }
    }

    /// <summary>Event-camera RAW player: file open / drag&amp;drop, play / pause, loop, speed, wheel zoom / pan,
    /// position slider and accumulation-time slider.</summary>
    public sealed class MainForm : Form, IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int MK_SHIFT = 0x0004;
        private const int MK_CONTROL = 0x0008;

        // speed slider: value 0..200  ->  speed = 10^((value-100)/50)  (0.01x .. 100x, 1x at 100)
        private const int SpeedCenter = 100;
        private const double SpeedDecadeSteps = 50.0;
        private const int SeekMax = 10000;

        private IntPtr _h = IntPtr.Zero;
        private int _vw, _vh;
        private long _startUs, _endUs;
        private Bitmap _bmp;

        private readonly VideoView _pic = new VideoView();
        private readonly Label _hint = new Label();
        private readonly Button _btnOpen = new Button();
        private readonly Button _btnPlay = new Button();
        private readonly Button _btnHome = new Button();
        private readonly Button _btnSpeedReset = new Button();
        private readonly CheckBox _chkLoop = new CheckBox();
        private readonly TrackBar _tbSeek = new TrackBar();
        private readonly TrackBar _tbSpeed = new TrackBar();
        private readonly TrackBar _tbAcc = new TrackBar();
        private readonly Label _lblPath = new Label();
        private readonly Label _lblTime = new Label();
        private readonly Label _lblSpeed = new Label();
        private readonly Label _lblAcc = new Label();
        private readonly Timer _timer = new Timer();

        // ROI vibration analysis
        private sealed class RoiItem
        {
            public int Slot;
            public string Text = "";
            public override string ToString() { return Text; }
        }
        private const int MaxRoi = 8;
        private static readonly Color[] RoiColors =
        {
            Color.Lime, Color.Cyan, Color.Orange, Color.Magenta, Color.Yellow, Color.DeepSkyBlue, Color.Tomato, Color.White
        };
        private readonly Rectangle[] _roiRect = new Rectangle[MaxRoi];
        private readonly bool[] _roiUsed = new bool[MaxRoi];
        private readonly ListBox _lstRoi = new ListBox();
        private readonly SpectrumView _spec = new SpectrumView();
        private readonly WaveView _wave = new WaveView();
        private readonly NumericUpDown _numScale = new NumericUpDown();
        private readonly CheckBox _chkRoiMode = new CheckBox();
        private readonly Button _btnRoiClear = new Button();
        private readonly NumericUpDown _numWindow = new NumericUpDown();
        private readonly NumericUpDown _numMaxHz = new NumericUpDown();
        private int _anTick;

        private bool _dragging;
        private long _pendingSeekUs = long.MinValue;
        private DateTime _lastSeek = DateTime.MinValue;

        public MainForm(string initialFile)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "MV RAW Player";
            ClientSize = new Size(1380, 860);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            BuildUi();

            _timer.Interval = 33;
            _timer.Tick += OnTick;
            _timer.Start();

            Application.AddMessageFilter(this);
            FormClosing += (s, e) =>
            {
                Application.RemoveMessageFilter(this);
                CloseFile();
            };

            if (!string.IsNullOrEmpty(initialFile) && File.Exists(initialFile))
                Shown += (s, e) => OpenFile(initialFile);
        }

        // ------------------------------------------------------------------ UI construction

        private Control BuildAnalysisPanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 360, Padding = new Padding(6, 4, 6, 6) };

            _pic.RoiDrawn += OnRoiDrawn;
            _pic.RoiRemoveRequested += RemoveRoi;

            _spec.Dock = DockStyle.Fill;

            var settings = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 92, ColumnCount = 2, RowCount = 3 };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            _numWindow.Minimum = 0.5m; _numWindow.Maximum = 10m; _numWindow.DecimalPlaces = 1;
            _numWindow.Increment = 0.5m; _numWindow.Value = 1.0m; _numWindow.Dock = DockStyle.Fill;
            _numMaxHz.Minimum = 100; _numMaxHz.Maximum = 2000; _numMaxHz.Increment = 100;
            _numMaxHz.Value = 500; _numMaxHz.Dock = DockStyle.Fill;
            _numWindow.ValueChanged += (s, e) => ApplyAnalysisConfig();
            _numMaxHz.ValueChanged += (s, e) => ApplyAnalysisConfig();
            settings.Controls.Add(MakeLabel("분석 구간 길이 (초)"), 0, 0);
            settings.Controls.Add(_numWindow, 1, 0);
            settings.Controls.Add(MakeLabel("최대 분석 주파수 (Hz)"), 0, 1);
            settings.Controls.Add(_numMaxHz, 1, 1);
            _numScale.Minimum = 0; _numScale.Maximum = 1000; _numScale.DecimalPlaces = 4;
            _numScale.Increment = 0.01m; _numScale.Value = 0; _numScale.Dock = DockStyle.Fill;
            settings.Controls.Add(MakeLabel("화면 배율 (mm/픽셀, 0=사용 안 함)"), 0, 2);
            settings.Controls.Add(_numScale, 1, 2);

            _lstRoi.Dock = DockStyle.Top;
            _lstRoi.Height = 100;
            _lstRoi.IntegralHeight = false;
            _lstRoi.Font = new Font("Consolas", 10f);
            _lstRoi.SelectedIndexChanged += (s, e) => UpdateAnalysis(true);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
            _chkRoiMode.Text = "▭ ROI 지정";
            _chkRoiMode.Appearance = Appearance.Button;
            _chkRoiMode.TextAlign = ContentAlignment.MiddleCenter;
            _chkRoiMode.Width = 110;
            _chkRoiMode.Height = 30;
            _chkRoiMode.CheckedChanged += (s, e) => _pic.RoiMode = _chkRoiMode.Checked;
            _btnRoiClear.Text = "모두 지우기";
            _btnRoiClear.Width = 100;
            _btnRoiClear.Height = 30;
            _btnRoiClear.Click += (s, e) => ClearRois();
            bar.Controls.Add(_chkRoiMode);
            bar.Controls.Add(_btnRoiClear);

            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Text = "진동 분석 (ROI별 주파수)",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var help = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                ForeColor = Color.DimGray,
                Text = "Shift+드래그 또는 [ROI 지정] 후 드래그로 영역 지정\n영역 우클릭: 삭제 · 최대 8개 · 진폭은 영역마다 물체 1개일 때 정확\n구간이 길수록 주파수 분해능이 좋아집니다",
            };

            _wave.Dock = DockStyle.Bottom;
            _wave.Height = 110;

            panel.Controls.Add(_spec);
            panel.Controls.Add(_wave);
            panel.Controls.Add(help);
            panel.Controls.Add(settings);
            panel.Controls.Add(_lstRoi);
            panel.Controls.Add(bar);
            panel.Controls.Add(title);
            return panel;
        }

        // ---- ROI vibration analysis ----

        private void ApplyAnalysisConfig()
        {
            if (_h != IntPtr.Zero)
                Native.mvp_an_config(_h, (int)_numMaxHz.Value, (int)(_numWindow.Value * 1000m));
        }

        private void OnRoiDrawn(Rectangle r)
        {
            if (_h == IntPtr.Zero)
                return;
            int slot = -1;
            for (int i = 0; i < MaxRoi; ++i)
                if (!_roiUsed[i]) { slot = i; break; }
            if (slot < 0)
            {
                MessageBox.Show(this, "ROI는 최대 " + MaxRoi + "개까지 지정할 수 있습니다. 우클릭으로 삭제하세요.", "ROI",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _roiUsed[slot] = true;
            _roiRect[slot] = r;
            Native.mvp_roi_set(_h, slot, r.Left, r.Top, r.Right, r.Bottom);
            _pic.Rois.Add(new VideoView.RoiOverlay { R = r, Slot = slot, Color = RoiColors[slot % RoiColors.Length], Label = "R" + (slot + 1) });
            var item = new RoiItem { Slot = slot, Text = "R" + (slot + 1) + "  --" };
            int idx = _lstRoi.Items.Add(item);
            _lstRoi.SelectedIndex = idx;
            _pic.Invalidate();
        }

        private void RemoveRoi(int slot)
        {
            if (slot < 0 || slot >= MaxRoi || !_roiUsed[slot])
                return;
            _roiUsed[slot] = false;
            if (_h != IntPtr.Zero)
                Native.mvp_roi_set(_h, slot, 0, 0, 0, 0);
            _pic.Rois.RemoveAll(o => o.Slot == slot);
            for (int i = 0; i < _lstRoi.Items.Count; ++i)
            {
                if (((RoiItem)_lstRoi.Items[i]).Slot == slot)
                {
                    _lstRoi.Items.RemoveAt(i);
                    break;
                }
            }
            if (_lstRoi.Items.Count > 0 && _lstRoi.SelectedIndex < 0)
                _lstRoi.SelectedIndex = 0;
            if (_lstRoi.Items.Count == 0)
            {
                _spec.Clear();
                _wave.Clear();
            }
            _pic.Invalidate();
        }

        private void ClearRois()
        {
            if (_h != IntPtr.Zero)
                Native.mvp_roi_clear_all(_h);
            for (int i = 0; i < MaxRoi; ++i)
                _roiUsed[i] = false;
            _pic.Rois.Clear();
            _lstRoi.Items.Clear();
            _spec.Clear();
            _wave.Clear();
            _pic.Invalidate();
        }

        private bool _inUpdate;

        private void UpdateAnalysis(bool force)
        {
            if (_inUpdate || _h == IntPtr.Zero || _lstRoi.Items.Count == 0)
                return;
            _inUpdate = true;
            try
            {
                UpdateAnalysisCore();
            }
            finally
            {
                _inUpdate = false;
            }
        }

        private void UpdateAnalysisCore()
        {
            var hz = new double[4];
            var st = new double[4];
            for (int i = 0; i < _lstRoi.Items.Count; ++i)
            {
                var it = (RoiItem)_lstRoi.Items[i];
                double total;
                int n = Native.mvp_roi_peaks(_h, it.Slot, hz, st, 4, out total);
                string txt = "R" + (it.Slot + 1) + "  ";
                string amp = "";
                double fshow = n > 0 ? hz[0] : 0;
                if (n > 0)
                {
                    var am = new double[8];
                    int used = Native.mvp_roi_amp(_h, it.Slot, hz[0], am);
                    double a = Math.Max(am[0], am[1]);
                    double r2 = am[0] >= am[1] ? am[5] : am[6];
                    if (used > 0 && r2 >= 0.1 && Math.Abs(am[7] - hz[0]) <= 1.6)
                    {
                        fshow = am[7];
                        amp = "±" + a.ToString(a < 10 ? "0.0#" : "0.0") + "px" + (am[0] >= am[1] ? " X" : " Y");
                        double mm = (double)_numScale.Value;
                        if (mm > 0)
                            amp += " (" + (a * mm).ToString("0.###") + "mm)";
                    }
                    var parts = new List<string>();
                    parts.Add(fshow.ToString("0.0"));
                    for (int k = 1; k < Math.Min(n, 3); ++k)
                        parts.Add(hz[k].ToString("0.0"));
                    txt += string.Join("/", parts.ToArray()) + "Hz";
                    if (amp.Length > 0)
                        txt += "  " + amp;
                }
                else
                {
                    txt += total > 0 ? "(뚜렷한 진동 없음)" : "--";
                }
                if (txt != it.Text)
                {
                    it.Text = txt;
                    _lstRoi.Items[i] = it; // refresh text
                }
                foreach (VideoView.RoiOverlay ro in _pic.Rois)
                    if (ro.Slot == it.Slot)
                        ro.Label = n > 0 ? "R" + (it.Slot + 1) + " " + fshow.ToString("0.0") + " Hz" + (amp.Length > 0 ? " " + amp.Replace(" X", "").Replace(" Y", "") : "") : "R" + (it.Slot + 1);
            }
            _pic.Invalidate();

            int sel = _lstRoi.SelectedIndex;
            if (sel < 0 && _lstRoi.Items.Count > 0)
                return;
            var cur = (RoiItem)_lstRoi.Items[sel];
            var buf = new float[(int)_numMaxHz.Value + 1];
            int nb = Native.mvp_roi_hist(_h, cur.Slot, buf, buf.Length);
            if (nb > 0)
            {
                double total2;
                int np = Native.mvp_roi_peaks(_h, cur.Slot, hz, st, 4, out total2);
                var pk = new double[Math.Max(0, np)];
                Array.Copy(hz, pk, pk.Length);
                _spec.SetData(buf, nb, pk, "R" + (cur.Slot + 1) + "  주파수 분포 (표본 " + ((long)total2) + ")",
                    RoiColors[cur.Slot % RoiColors.Length]);
            }
            var tx = new float[800]; var vx = new float[800]; var ty = new float[800]; var vy = new float[800];
            double wms, wms2;
            int nx = Native.mvp_roi_series(_h, cur.Slot, 0, 800, tx, vx, out wms);
            int ny = Native.mvp_roi_series(_h, cur.Slot, 1, 800, ty, vy, out wms2);
            if (nx > 1)
                _wave.SetData(tx, vx, nx, ty, vy, Math.Max(0, ny), wms, (double)_numScale.Value);
            else
                _wave.Clear();
        }


        private void BuildUi()
        {
            // top bar
            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 4) };
            _btnOpen.Text = "RAW 파일 열기...";
            _btnOpen.Width = 140;
            _btnOpen.Dock = DockStyle.Left;
            _btnOpen.Click += (s, e) => ChooseFile();
            _lblPath.Dock = DockStyle.Fill;
            _lblPath.TextAlign = ContentAlignment.MiddleLeft;
            _lblPath.Padding = new Padding(10, 0, 0, 0);
            _lblPath.AutoEllipsis = true;
            _lblPath.Text = "파일을 선택하거나 이 창에 끌어다 놓으세요 (.raw / .dat / .hdf5)";
            top.Controls.Add(_lblPath);
            top.Controls.Add(_btnOpen);

            // video area
            _pic.Dock = DockStyle.Fill;
            _pic.BackColor = Color.FromArgb(24, 26, 30);
            _pic.AllowDrop = true;
            _pic.WheelMoved += (d, pt) => HandleWheel(d, pt, (ModifierKeys & Keys.Control) != 0, (ModifierKeys & Keys.Shift) != 0);
            _pic.DragEnter += OnDragEnter;
            _pic.DragDrop += OnDragDrop;
            _hint.Dock = DockStyle.Fill;
            _hint.TextAlign = ContentAlignment.MiddleCenter;
            _hint.ForeColor = Color.Gainsboro;
            _hint.BackColor = Color.Transparent;
            _hint.Font = new Font("Segoe UI", 13f);
            _hint.Text = "RAW 파일을 여기로 끌어다 놓거나\n[RAW 파일 열기] 버튼으로 선택하세요";
            _hint.AllowDrop = true;
            _hint.DragEnter += OnDragEnter;
            _hint.DragDrop += OnDragDrop;
            _pic.Controls.Add(_hint);

            // bottom controls
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(8, 4, 8, 8) };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 4,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            // row 0: position slider
            grid.Controls.Add(MakeLabel("재생 위치(누적 시간)"), 0, 0);
            _tbSeek.Dock = DockStyle.Fill;
            _tbSeek.Minimum = 0;
            _tbSeek.Maximum = SeekMax;
            _tbSeek.TickStyle = TickStyle.None;
            _tbSeek.LargeChange = SeekMax / 20;
            _tbSeek.SmallChange = SeekMax / 200;
            _tbSeek.MouseDown += (s, e) => _dragging = true;
            _tbSeek.MouseUp += (s, e) =>
            {
                _dragging = false;
                SeekToSlider();
            };
            _tbSeek.Scroll += (s, e) =>
            {
                _pendingSeekUs = SliderToUs();
                UpdateTimeLabel(_pendingSeekUs);
                if (!_dragging)
                    SeekToSlider();
            };
            grid.Controls.Add(_tbSeek, 1, 0);
            _lblTime.Dock = DockStyle.Fill;
            _lblTime.TextAlign = ContentAlignment.MiddleLeft;
            _lblTime.Font = new Font("Consolas", 10f);
            _lblTime.Text = "00:00.000 / 00:00.000";
            grid.Controls.Add(_lblTime, 2, 0);

            // row 1: transport buttons
            var transport = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _btnPlay.Text = "▶ 재생";
            _btnPlay.Width = 120;
            _btnPlay.Height = 30;
            _btnPlay.Click += (s, e) => TogglePlay();
            _btnHome.Text = "⏮ 처음";
            _btnHome.Width = 90;
            _btnHome.Height = 30;
            _btnHome.Click += (s, e) =>
            {
                if (_h != IntPtr.Zero)
                    Native.mvp_seek(_h, _startUs);
            };
            _chkLoop.Text = "반복 재생";
            _chkLoop.AutoSize = true;
            _chkLoop.Margin = new Padding(16, 7, 0, 0);
            _chkLoop.CheckedChanged += (s, e) =>
            {
                if (_h != IntPtr.Zero)
                    Native.mvp_set_loop(_h, _chkLoop.Checked ? 1 : 0);
            };
            transport.Controls.Add(_btnPlay);
            transport.Controls.Add(_btnHome);
            transport.Controls.Add(_chkLoop);
            grid.Controls.Add(MakeLabel("재생 제어"), 0, 1);
            grid.Controls.Add(transport, 1, 1);
            grid.SetColumnSpan(transport, 2);

            // row 2: speed
            grid.Controls.Add(MakeLabel("재생 속도"), 0, 2);
            _tbSpeed.Dock = DockStyle.Fill;
            _tbSpeed.Minimum = 0;
            _tbSpeed.Maximum = 200;
            _tbSpeed.Value = SpeedCenter;
            _tbSpeed.TickFrequency = 50;
            _tbSpeed.SmallChange = 2;
            _tbSpeed.LargeChange = 10;
            _tbSpeed.ValueChanged += (s, e) => ApplySpeed();
            grid.Controls.Add(_tbSpeed, 1, 2);
            var speedBox = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _lblSpeed.AutoSize = false;
            _lblSpeed.Width = 80;
            _lblSpeed.Height = 28;
            _lblSpeed.TextAlign = ContentAlignment.MiddleLeft;
            _lblSpeed.Font = new Font("Consolas", 10f);
            _btnSpeedReset.Text = "1x";
            _btnSpeedReset.Width = 44;
            _btnSpeedReset.Height = 26;
            _btnSpeedReset.Click += (s, e) => _tbSpeed.Value = SpeedCenter;
            speedBox.Controls.Add(_lblSpeed);
            speedBox.Controls.Add(_btnSpeedReset);
            grid.Controls.Add(speedBox, 2, 2);

            // row 3: accumulation time
            grid.Controls.Add(MakeLabel("프레임 누적 시간"), 0, 3);
            _tbAcc.Dock = DockStyle.Fill;
            _tbAcc.Minimum = 1;
            _tbAcc.Maximum = 300;
            _tbAcc.Value = 20;
            _tbAcc.TickFrequency = 25;
            _tbAcc.SmallChange = 1;
            _tbAcc.LargeChange = 10;
            _tbAcc.ValueChanged += (s, e) => ApplyAccumulation();
            grid.Controls.Add(_tbAcc, 1, 3);
            _lblAcc.Dock = DockStyle.Fill;
            _lblAcc.TextAlign = ContentAlignment.MiddleLeft;
            _lblAcc.Font = new Font("Consolas", 10f);
            grid.Controls.Add(_lblAcc, 2, 3);

            bottom.Controls.Add(grid);

            var hintBar = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Text = "마우스 휠: 확대/축소  |  드래그: 이동  |  더블클릭 또는 0: 화면 맞춤  |  Shift+드래그: ROI  |  Ctrl+휠: 재생 속도  |  Shift+휠: 누적 시간  |  Space: 재생/일시정지  |  ←/→: ±1초",
            };

            Controls.Add(_pic);
            Controls.Add(BuildAnalysisPanel());
            Controls.Add(top);
            Controls.Add(bottom);
            Controls.Add(hintBar);
            ApplySpeed();
            ApplyAccumulation();
            UpdateEnabled();
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }

        // ------------------------------------------------------------------ file handling

        private void ChooseFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "이벤트 카메라 RAW 파일 선택";
                dlg.Filter = "Event files (*.raw;*.dat;*.hdf5)|*.raw;*.dat;*.hdf5|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    OpenFile(dlg.FileName);
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
                OpenFile(files[0]);
        }

        private void OpenFile(string path)
        {
            CloseFile();
            int w, h;
            long start, end;
            IntPtr handle;
            Cursor = Cursors.WaitCursor;
            try
            {
                handle = Native.mvp_open(path, out w, out h, out start, out end);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "재생 라이브러리를 불러오지 못했습니다.\n\n" + ex.Message, "오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Cursor = Cursors.Default;
            if (handle == IntPtr.Zero)
            {
                MessageBox.Show(this,
                    "파일을 열 수 없습니다.\n\n" + path + "\n\n" + Native.LastError() +
                    "\n\nMV_HAL_PLUGIN_PATH = " + Environment.GetEnvironmentVariable("MV_HAL_PLUGIN_PATH"),
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _h = handle;
            _vw = w;
            _vh = h;
            _startUs = start;
            _endUs = Math.Max(end, start + 1);
            _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            _pic.Image = _bmp;
            _hint.Visible = false;
            _lblPath.Text = path;
            Text = Path.GetFileName(path) + "  (" + w + "x" + h + ")  -  MV RAW Player";

            ClearRois();
            ApplyAnalysisConfig();
            Native.mvp_set_loop(_h, _chkLoop.Checked ? 1 : 0);
            ApplySpeed();
            ApplyAccumulation();
            _tbSeek.Value = 0;
            UpdateEnabled();
        }

        private void CloseFile()
        {
            ClearRois();
            if (_h != IntPtr.Zero)
            {
                IntPtr h = _h;
                _h = IntPtr.Zero;
                Native.mvp_close(h);
            }
            _pic.Image = null;
            if (_bmp != null)
            {
                _bmp.Dispose();
                _bmp = null;
            }
            _pendingSeekUs = long.MinValue;
            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            bool on = _h != IntPtr.Zero;
            _btnPlay.Enabled = on;
            _btnHome.Enabled = on;
            _tbSeek.Enabled = on;
            if (!on)
            {
                _hint.Visible = true;
                _btnPlay.Text = "▶ 재생";
                _lblTime.Text = "00:00.000 / 00:00.000";
            }
        }

        // ------------------------------------------------------------------ playback controls

        private void TogglePlay()
        {
            if (_h == IntPtr.Zero)
                return;
            if (Native.mvp_is_ended(_h) != 0)
                Native.mvp_set_playing(_h, 1); // restarts from the beginning
            else
                Native.mvp_set_playing(_h, Native.mvp_is_playing(_h) != 0 ? 0 : 1);
        }

        private double CurrentSpeed()
        {
            return Math.Pow(10.0, (_tbSpeed.Value - SpeedCenter) / SpeedDecadeSteps);
        }

        private void ApplySpeed()
        {
            double s = CurrentSpeed();
            _lblSpeed.Text = s.ToString(s >= 10 ? "0" : (s >= 1 ? "0.0" : "0.00")) + "x";
            if (_h != IntPtr.Zero)
                Native.mvp_set_speed(_h, s);
        }

        private void ApplyAccumulation()
        {
            _lblAcc.Text = _tbAcc.Value + " ms";
            if (_h != IntPtr.Zero)
                Native.mvp_set_accumulation_us(_h, _tbAcc.Value * 1000L);
        }

        private long SliderToUs()
        {
            double f = _tbSeek.Value / (double)SeekMax;
            return _startUs + (long)(f * (_endUs - _startUs));
        }

        private void SeekToSlider()
        {
            if (_h == IntPtr.Zero)
                return;
            Native.mvp_seek(_h, SliderToUs());
            _pendingSeekUs = long.MinValue;
            _lastSeek = DateTime.UtcNow;
        }

        private void StepSeconds(double seconds)
        {
            if (_h == IntPtr.Zero)
                return;
            long pos = Native.mvp_get_position_us(_h);
            long target = pos + (long)(seconds * 1e6);
            target = Math.Max(_startUs, Math.Min(_endUs, target));
            Native.mvp_seek(_h, target);
        }

        private static string FormatUs(long us)
        {
            if (us < 0)
                us = 0;
            TimeSpan t = TimeSpan.FromTicks(us * 10);
            return string.Format("{0:00}:{1:00}.{2:000}", (int)t.TotalMinutes, t.Seconds, t.Milliseconds);
        }

        private void UpdateTimeLabel(long posUs)
        {
            _lblTime.Text = FormatUs(posUs - _startUs) + " / " + FormatUs(_endUs - _startUs);
        }

        // ------------------------------------------------------------------ frame loop

        private void OnTick(object sender, EventArgs e)
        {
            if (_h == IntPtr.Zero || _bmp == null)
                return;

            BitmapData data = _bmp.LockBits(new Rectangle(0, 0, _vw, _vh), ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                Native.mvp_render_bgra(_h, data.Scan0, data.Stride);
            }
            finally
            {
                _bmp.UnlockBits(data);
            }
            _pic.Invalidate();

            if (++_anTick >= 8)
            {
                _anTick = 0;
                UpdateAnalysis(false);
            }

            long pos = Native.mvp_get_position_us(_h);
            if (!_dragging)
            {
                double f = (pos - _startUs) / (double)(_endUs - _startUs);
                int v = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, f)) * SeekMax);
                if (v != _tbSeek.Value)
                    _tbSeek.Value = v;
                UpdateTimeLabel(pos);
            }
            else if (_pendingSeekUs != long.MinValue && (DateTime.UtcNow - _lastSeek).TotalMilliseconds > 120)
            {
                // live preview while dragging the thumb
                Native.mvp_seek(_h, _pendingSeekUs);
                _pendingSeekUs = long.MinValue;
                _lastSeek = DateTime.UtcNow;
            }

            bool ended = Native.mvp_is_ended(_h) != 0;
            bool playing = Native.mvp_is_playing(_h) != 0;
            _btnPlay.Text = ended ? "↻ 다시 재생" : (playing ? "⏸ 일시정지" : "▶ 재생");
        }

        // ------------------------------------------------------------------ keyboard / mouse wheel

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space && !(ActiveControl is TextBoxBase))
            {
                TogglePlay();
                return true;
            }
            if ((keyData == Keys.D0 || keyData == Keys.NumPad0) && !(ActiveControl is TextBoxBase))
            {
                _pic.ResetView();
                return true;
            }
            if ((keyData == Keys.Left || keyData == Keys.Right) && !(ActiveControl is TrackBar))
            {
                StepSeconds(keyData == Keys.Left ? -1.0 : 1.0);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void HandleWheel(int delta, Point pt, bool ctrl, bool shift)
        {
            int steps = Math.Max(1, Math.Abs(delta) / 120);
            int dir = delta > 0 ? 1 : -1;
            if (ctrl)
                _tbSpeed.Value = Math.Max(_tbSpeed.Minimum, Math.Min(_tbSpeed.Maximum, _tbSpeed.Value + dir * 4 * steps));
            else if (shift)
                _tbAcc.Value = Math.Max(_tbAcc.Minimum, Math.Min(_tbAcc.Maximum, _tbAcc.Value + dir * 5 * steps));
            else
                _pic.ZoomAt(pt, Math.Pow(1.2, delta / 120.0));
        }

        /// <summary>Mouse wheel over the video: zoom in/out around the cursor
        /// (Ctrl: playback speed, Shift: accumulation time).</summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || Form.ActiveForm != this)
                return false;
            Rectangle r = _pic.RectangleToScreen(_pic.ClientRectangle);
            if (!r.Contains(Cursor.Position))
                return false;

            long wp = m.WParam.ToInt64();
            int delta = (short)((wp >> 16) & 0xFFFF);
            HandleWheel(delta, _pic.PointToClient(Cursor.Position), (wp & MK_CONTROL) != 0, (wp & MK_SHIFT) != 0);
            return true;
        }
    }
}
