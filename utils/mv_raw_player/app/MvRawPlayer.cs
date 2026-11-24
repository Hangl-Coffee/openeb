using System;
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
    }

    /// <summary>Video surface with mouse-wheel zoom (around the cursor), drag panning and double-click reset.</summary>
    internal sealed class VideoView : Control
    {
        private const double MinZoom = 0.25;
        private const double MaxZoom = 64.0;

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
            if (e.Button == MouseButtons.Left && _image != null)
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
            _panning = false;
            Cursor = Cursors.Default;
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

        private bool _dragging;
        private long _pendingSeekUs = long.MinValue;
        private DateTime _lastSeek = DateTime.MinValue;

        public MainForm(string initialFile)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "MV RAW Player";
            ClientSize = new Size(1100, 760);
            MinimumSize = new Size(760, 520);
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
                Text = "마우스 휠: 확대/축소  |  드래그: 이동  |  더블클릭 또는 0: 화면 맞춤  |  Ctrl+휠: 재생 속도  |  Shift+휠: 누적 시간  |  Space: 재생/일시정지  |  ←/→: ±1초",
            };

            Controls.Add(_pic);
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

            Native.mvp_set_loop(_h, _chkLoop.Checked ? 1 : 0);
            ApplySpeed();
            ApplyAccumulation();
            _tbSeek.Value = 0;
            UpdateEnabled();
        }

        private void CloseFile()
        {
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
