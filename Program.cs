/*
 * GpuVramMonitor — RTX 3090 Neptune VRAM 温度监控 & 风扇控制
 *
 * 功能:
 *   1. 桌面悬浮窗实时显示 VRAM 温度 (Layered Window, 完美抗锯齿圆角)
 *   2. 可拖动滑块手动调节风扇转速 + 实时 RPM 显示
 *   3. 自动/手动模式切换（点击 Auto/Manual 标签）
 *   4. 3°C 滞回防止风扇抖动
 *   5. 系统托盘图标
 *
 * GDDR6X 温度参考:
 *   < 70°C : 安全, 静音优先
 *   70-85°C: AI 推理正常工作区间
 *   85-95°C: 偏高, 加强散热
 *   > 95°C : 过热, 全速
 *   110°C  : NVIDIA 降频阈值
 */

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

namespace GpuVramMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "GpuVramMonitor_SingleInstance", out bool created);
        if (!created) { MessageBox.Show("GPU VRAM Monitor 已在运行中", "提示"); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MonitorApp());
    }
}

// ════════════════════════════════════════════════════════════
//  MonitorApp — 主控制器
// ════════════════════════════════════════════════════════════
class MonitorApp : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly FanController _fanCtrl;
    private readonly TemperatureOverlay _overlay;
    private readonly string _logPath;

    private double _lastVramTemp = -1, _lastCoreTemp = -1, _lastPowerW = -1, _lastPowerLimitW = -1, _lastCoreClockMhz = -1, _lastRpm = -1;
    private int _lastFanPct = -1, _errorCount = 0;
    private int _currentTargetPct = 30;
    private const int Hysteresis = 3;
    private DateTime _startTime;
    private DateTime _lastPowerLimitQuery = DateTime.MinValue;

    // Manual override
    private bool _manualMode = false;
    private int _manualPct = 50;

    public MonitorApp()
    {
        _startTime = DateTime.Now;
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vram-monitor.log");

        _fanCtrl = new FanController();
        var initOk = _fanCtrl.Initialize();

        // Create overlay with callbacks
        _overlay = new TemperatureOverlay();
        _overlay.OnManualSpeedChanged += (pct) =>
        {
            _manualMode = true;
            _manualPct = pct;
            if (_fanCtrl.IsReady)
            {
                _fanCtrl.SetPumpFanSpeed(pct);
                Thread.Sleep(200);
                var rpm = _fanCtrl.GetPumpRpm();
                _lastRpm = rpm;
                _overlay.UpdateRpm(rpm);
            }
        };
        _overlay.OnAutoModeRequested += () =>
        {
            _manualMode = false;
            _lastFanPct = -1; // force recalc
        };
        _overlay.OnPowerLimitRequested += SetPowerLimitPreset;
        _overlay.Show();

        _trayIcon = new NotifyIcon
        {
            Visible = true,
            Text = "GPU VRAM Monitor - 初始化中...",
            ContextMenuStrip = CreateContextMenu(),
            Icon = CreateTempIcon(-1, Color.Gray)
        };
        _trayIcon.DoubleClick += (s, e) => _overlay.ToggleVisibility();

        if (!initOk)
            _trayIcon.ShowBalloonTip(5000, "GPU VRAM Monitor",
                "⚠ 无法初始化硬件控制,请确保以管理员身份运行", ToolTipIcon.Warning);

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
        Log("启动");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var vram = GpuSensorReader.ReadGpuVramTemp();
        var core = GpuSensorReader.ReadGpuCoreTemp();
        var power = GpuSensorReader.ReadGpuPower();
        var coreClock = GpuSensorReader.ReadGpuCoreClock();
        RefreshPowerLimit();

        if (vram < 0)
        {
            _errorCount++;
            if (_errorCount == 1 || _errorCount % 20 == 0)
            {
                _trayIcon.Icon = CreateTempIcon(-1, Color.Red);
                _trayIcon.Text = "GPU VRAM Monitor - sensor unavailable";
            }
            _overlay.UpdateData(vram, core, power, _lastPowerLimitW, coreClock, _lastFanPct, _lastRpm, !_manualMode);
            return;
        }

        _errorCount = 0;
        _lastVramTemp = vram;
        _lastCoreTemp = core;
        _lastPowerW = power;
        _lastCoreClockMhz = coreClock;

        int targetPct = _manualMode ? _manualPct : CalcFanSpeed(vram);

        // Apply fan speed
        if (Math.Abs(targetPct - _lastFanPct) >= 3 || _lastFanPct == -1)
        {
            if (_fanCtrl.IsReady)
            {
                _fanCtrl.SetPumpFanSpeed(targetPct);
                _lastFanPct = targetPct;
                Log($"VRAM={vram:F1}°C Fan→{targetPct}% {(_manualMode ? "手动" : "自动")}");
            }
        }

        _lastRpm = _fanCtrl.GetPumpRpm();

        // Update tray
        var color = vram switch
        {
            >= 95 => Color.Red, >= 85 => Color.OrangeRed,
            >= 75 => Color.Orange, >= 65 => Color.YellowGreen,
            _ => Color.LimeGreen
        };
        _trayIcon.Icon = CreateTempIcon((int)Math.Round(vram), color);
        _trayIcon.Text = $"VRAM {vram:F0}°C | {power:F0}W {coreClock:F0}MHz | Fan {_lastFanPct}%";

        // Update overlay
        _overlay.UpdateData(vram, core, power, _lastPowerLimitW, coreClock, _lastFanPct, _lastRpm, !_manualMode);
    }

    private void RefreshPowerLimit(bool force = false)
    {
        if (!force && DateTime.Now - _lastPowerLimitQuery < TimeSpan.FromSeconds(15)) return;

        var limit = NvidiaPowerLimit.GetPowerLimit();
        _lastPowerLimitQuery = DateTime.Now;
        if (limit > 0) _lastPowerLimitW = limit;
    }

    private void SetPowerLimitPreset(int target)
    {
        if (NvidiaPowerLimit.SetPowerLimit(target))
        {
            _lastPowerLimitW = target;
            _lastPowerLimitQuery = DateTime.Now;
            _overlay.UpdatePowerLimit(_lastPowerLimitW);
            Log($"PowerLimit→{target}W");
        }
        else
        {
            _trayIcon.ShowBalloonTip(3000, "GPU VRAM Monitor", $"无法切换功率限制到 {target}W", ToolTipIcon.Warning);
        }
    }

    // ── Fan curve with hysteresis ──
    private int CalcFanSpeed(double temp)
    {
        (double t, int p)[] curve = [
            (45, 30), (55, 38), (62, 45), (68, 50), (72, 58),
            (76, 65), (80, 72), (84, 80), (88, 90), (92, 95), (95, 100)
        ];

        int raw;
        if (temp <= curve[0].t) raw = curve[0].p;
        else if (temp >= curve[^1].t) raw = curve[^1].p;
        else
        {
            raw = curve[^1].p;
            for (int i = 0; i < curve.Length - 1; i++)
            {
                if (temp >= curve[i].t && temp < curve[i + 1].t)
                {
                    double r = (temp - curve[i].t) / (curve[i + 1].t - curve[i].t);
                    raw = (int)(curve[i].p + (curve[i + 1].p - curve[i].p) * r);
                    break;
                }
            }
        }

        if (raw > _currentTargetPct) _currentTargetPct = raw;
        else if (raw < _currentTargetPct)
        {
            double tForCurrent = 0;
            for (int i = 0; i < curve.Length - 1; i++)
            {
                if (_currentTargetPct >= curve[i].p && _currentTargetPct <= curve[i + 1].p)
                {
                    double r = (_currentTargetPct - curve[i].p) / (double)(curve[i + 1].p - curve[i].p);
                    tForCurrent = curve[i].t + r * (curve[i + 1].t - curve[i].t);
                    break;
                }
            }
            if (temp < tForCurrent - Hysteresis) _currentTargetPct = raw;
        }
        return Math.Clamp(_currentTargetPct, 30, 100);
    }

    private Icon CreateTempIcon(int temp, Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);
        string text = temp < 0 ? "?" : temp.ToString();
        float fs = text.Length >= 3 ? 7.5f : 9f;
        using var font = new Font("Segoe UI", fs, FontStyle.Bold);
        using var brush = new SolidBrush(color);
        using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, shadow, new RectangleF(1, 1, 16, 16), sf);
        g.DrawString(text, font, brush, new RectangleF(0, 0, 16, 16), sf);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        var ov = new ToolStripMenuItem("🖥 桌面温度窗") { Font = new Font("Segoe UI", 9, FontStyle.Bold), Checked = true };
        ov.Click += (s, e) => { _overlay.ToggleVisibility(); ((ToolStripMenuItem)s!).Checked = _overlay.Visible; };
        menu.Items.Add(ov);

        var st = new ToolStripMenuItem("📊 详细状态");
        st.Click += (s, e) => ShowStatus();
        menu.Items.Add(st);
        menu.Items.Add(new ToolStripSeparator());

        var ex = new ToolStripMenuItem("❌ 退出");
        ex.Click += (s, e) =>
        {
            if (MessageBox.Show("退出后风扇保持最后速度。\n确定退出吗？", "GPU VRAM Monitor",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _timer.Stop(); _fanCtrl.Dispose(); _overlay.Close(); _trayIcon.Visible = false;
                Log("退出"); Application.Exit();
            }
        };
        menu.Items.Add(ex);
        return menu;
    }

    private void ShowStatus()
    {
        var up = DateTime.Now - _startTime;
        var mode = _manualMode ? $"手动 {_manualPct}%" : "自动曲线";
        MessageBox.Show($@"═══ GPU VRAM Monitor ═══

🌡 VRAM 显存:  {_lastVramTemp:F1}°C
🔲 GPU 核心:   {_lastCoreTemp:F1}°C
⚡ 功率:        {_lastPowerW:F0} W
🔌 功率限制:    {_lastPowerLimitW:F0} W
⏱ 核心频率:    {_lastCoreClockMhz:F0} MHz
🌀 冷排风扇:   {_lastFanPct}% ({_lastRpm:F0} RPM)
⚙ 模式:        {mode}
⏱ 运行时间:    {up.Hours}h {up.Minutes}m

═══ GDDR6X 温度参考 ═══
✅ < 70°C   安全 (静音)
🟡 70-85°C  正常 (AI推理)
🟠 85-95°C  偏高 (积极散热)
🔴 > 95°C   过热 (全速)", "GPU VRAM Monitor 状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { }
    }
}

// ════════════════════════════════════════════════════════════
//  TemperatureOverlay — Layered Window 桌面悬浮温度窗
//  使用 UpdateLayeredWindow 实现像素级 Alpha 透明
//  边角完全抗锯齿，无毛刺（苹果风格大圆角）
// ════════════════════════════════════════════════════════════
class TemperatureOverlay : Form
{
    private double _vramTemp = -1, _coreTemp = -1, _powerW = -1, _powerLimitW = -1, _coreClockMhz = -1, _rpm = -1;
    private int _fanPct = -1;
    private bool _isAutoMode = true;

    private bool _sliderDragging = false;
    private int _sliderValue = 50;
    private readonly int _sliderY = 112;
    private Rectangle _sliderTrack;
    private const int SliderH = 8, SliderKnobR = 7, Pad = 18, Radius = 24;

    private bool _winDrag = false;
    private Point _winDragStart;

    public event Action<int>? OnManualSpeedChanged;
    public event Action? OnAutoModeRequested;
    public event Action<int>? OnPowerLimitRequested;

    // ── Win32 P/Invoke for Layered Window ──
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    public TemperatureOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(330, 150);

        var scr = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(scr.Right - Width - 16, scr.Bottom - Height - 16);
        _sliderTrack = new Rectangle(Pad, _sliderY, Width - Pad * 2, SliderH);

        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseMove += OnMouseMove;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00080000 | 0x00000080 | 0x00000008 | 0x08000000;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // ── Public API ──
    public void UpdateData(double vram, double core, double powerW, double powerLimitW, double coreClockMhz, int fanPct, double rpm, bool autoMode)
    {
        _vramTemp = vram; _coreTemp = core; _powerW = powerW; _powerLimitW = powerLimitW; _coreClockMhz = coreClockMhz; _fanPct = fanPct; _rpm = rpm; _isAutoMode = autoMode;
        if (autoMode && !_sliderDragging) _sliderValue = fanPct;
        Render();
    }
    public void UpdateRpm(double rpm) { _rpm = rpm; Render(); }
    public void UpdatePowerLimit(double powerLimitW) { _powerLimitW = powerLimitW; Render(); }
    public void ToggleVisibility() { Visible = !Visible; if (Visible) Render(); }

    // ── Mouse ──
    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // Close button (top-right X)
        if (new Rectangle(Width - 28, 4, 22, 22).Contains(e.Location))
        { Visible = false; Render(); return; }
        if (PowerLimit280Rect().Contains(e.Location))
        { OnPowerLimitRequested?.Invoke(NvidiaPowerLimit.PowerLimit280W); return; }
        if (PowerLimit350Rect().Contains(e.Location))
        { OnPowerLimitRequested?.Invoke(NvidiaPowerLimit.PowerLimit350W); return; }
        var kx = PctToX(_sliderValue);
        if (new Rectangle(kx - SliderKnobR - 4, _sliderY - SliderKnobR, (SliderKnobR + 4) * 2, (SliderKnobR + 4) * 2).Contains(e.Location)
            || new Rectangle(_sliderTrack.X, _sliderY - 8, _sliderTrack.Width, 28).Contains(e.Location))
        { _sliderDragging = true; SetSlider(e.X); return; }
        if (AutoModeRect().Contains(e.Location) && !_isAutoMode)
        { OnAutoModeRequested?.Invoke(); return; }
        _winDrag = true; _winDragStart = e.Location;
    }
    private void OnMouseUp(object? s, MouseEventArgs e) { _sliderDragging = false; _winDrag = false; }
    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (_sliderDragging) { SetSlider(e.X); return; }
        if (_winDrag) { Left += e.X - _winDragStart.X; Top += e.Y - _winDragStart.Y; }
    }
    private void SetSlider(int mx)
    {
        _sliderValue = Math.Clamp((int)((mx - _sliderTrack.X) * 100.0 / _sliderTrack.Width), 0, 100);
        OnManualSpeedChanged?.Invoke(_sliderValue);
        Render();
    }
    private int PctToX(int p) => _sliderTrack.X + (int)(p / 100.0 * _sliderTrack.Width);
    private Rectangle AutoModeRect() => new Rectangle(Width - 98, 48, 82, 20);
    private Rectangle PowerLimit280Rect() => new Rectangle(Width - 100, 72, 42, 24);
    private Rectangle PowerLimit350Rect() => new Rectangle(Width - 54, 72, 42, 24);

    private void DrawPowerLimitButton(Graphics g, Rectangle rect, int watts)
    {
        bool active = _powerLimitW > 0 && Math.Abs(_powerLimitW - watts) <= 5;
        var fill = active ? Color.FromArgb(90, 80, 145, 230) : Color.FromArgb(45, 58, 64, 88);
        var border = active ? Color.FromArgb(115, 150, 200, 255) : Color.FromArgb(45, 255, 255, 255);
        var text = active ? Color.FromArgb(230, 240, 255) : Color.FromArgb(155, 170, 195);

        using (var br = new SolidBrush(fill))
            g.FillPath(br, RoundedRect(rect, 8));
        using (var pen = new Pen(border, 1f))
            g.DrawPath(pen, RoundedRect(rect, 8));

        using var font = new Font("Segoe UI", 7.2f, FontStyle.Bold);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString($"{watts}", font, new SolidBrush(text), rect, sf);
    }

    // ══════════════════════════════════════════════════
    //  Render to layered window (per-pixel ARGB = no jaggies)
    // ══════════════════════════════════════════════════
    private void Render()
    {
        if (!IsHandleCreated || !Visible) return;

        // Use standard ARGB (not premultiplied) so text rendering works properly
        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.CompositingQuality = CompositingQuality.HighQuality;

        // Fill entire background with opaque dark color (for ClearType)
        g.Clear(Color.FromArgb(255, 20, 20, 30));

        // Close button (X) top-right
        var closeRect = new Rectangle(Width - 28, 4, 22, 22);
        using (var closeBr = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
            g.FillEllipse(closeBr, closeRect);
        using (var xPen = new Pen(Color.FromArgb(180, 200, 200, 220), 1.5f))
        {
            g.DrawLine(xPen, closeRect.X + 7, closeRect.Y + 7, closeRect.Right - 7, closeRect.Bottom - 7);
            g.DrawLine(xPen, closeRect.Right - 7, closeRect.Y + 7, closeRect.X + 7, closeRect.Bottom - 7);
        }

        // ── Slider shapes ──
        using (var tBr = new SolidBrush(Color.FromArgb(40, 60, 60, 80)))
            g.FillPath(tBr, RoundedRect(_sliderTrack, 4));
        int fw = PctToX(_sliderValue) - _sliderTrack.X;
        if (fw > 0)
        {
            var fc = _isAutoMode ? Color.FromArgb(120, 70, 180, 110) : Color.FromArgb(150, 70, 150, 255);
            using var fBr2 = new SolidBrush(fc);
            g.FillPath(fBr2, RoundedRect(new Rectangle(_sliderTrack.X, _sliderY, Math.Max(fw, 4), SliderH), 4));
        }
        int kx = PctToX(_sliderValue), ky = _sliderY + SliderH / 2;
        var kc = _sliderDragging ? Color.FromArgb(255, 140, 200, 255) :
                 _isAutoMode ? Color.FromArgb(210, 110, 210, 140) : Color.FromArgb(210, 110, 170, 255);
        using (var kBr = new SolidBrush(kc))
            g.FillEllipse(kBr, kx - SliderKnobR, ky - SliderKnobR, SliderKnobR * 2, SliderKnobR * 2);
        using (var kPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1.2f))
            g.DrawEllipse(kPen, kx - SliderKnobR, ky - SliderKnobR, SliderKnobR * 2, SliderKnobR * 2);

        // ── Left GPU telemetry ──
        using var metricLabelF = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var metricValueF = new Font("Segoe UI", 9f, FontStyle.Bold);
        var metricLabelBr = new SolidBrush(Color.FromArgb(135, 155, 185));
        var metricValueBr = new SolidBrush(Color.FromArgb(225, 232, 245));
        int metricX = 14;
        void DrawMetric(string label, string value, int y, Brush? valueBrush = null)
        {
            g.DrawString(label, metricLabelF, metricLabelBr, metricX, y);
            g.DrawString(value, metricValueF, valueBrush ?? metricValueBr, metricX, y + 10);
        }

        DrawMetric("CORE", _coreTemp < 0 ? "--" : $"{_coreTemp:F0}°C", 10);
        DrawMetric("PWR", _powerW < 0 ? "--" : $"{_powerW:F0}W", 39);
        DrawMetric("CLK", _coreClockMhz < 0 ? "--" : $"{_coreClockMhz:F0}MHz", 68);

        // ── VRAM Temperature ──
        Color vc = _vramTemp switch
        {
            >= 95 => Color.FromArgb(255, 55, 55),
            >= 85 => Color.FromArgb(255, 110, 45),
            >= 75 => Color.FromArgb(255, 175, 45),
            >= 65 => Color.FromArgb(175, 220, 55),
            _ => Color.FromArgb(65, 225, 115)
        };
        string vt = _vramTemp < 0 ? "--" : $"{_vramTemp:F0}";
        using var bigF = new Font("Segoe UI", 32, FontStyle.Bold);
        int vx = 102;
        g.DrawString(vt, bigF, new SolidBrush(vc), vx, 4);
        var vs = g.MeasureString(vt, bigF);
        using var uF = new Font("Segoe UI", 11);
        g.DrawString("°C", uF, new SolidBrush(Color.FromArgb(200, vc)), vx + vs.Width - 6, 14);
        using var labF = new Font("Segoe UI", 7.5f);
        g.DrawString("VRAM 显存", labF, new SolidBrush(Color.FromArgb(150, 160, 190)), vx + 2, 52);

        // ── Right info ──
        using var iF = new Font("Segoe UI", 8.5f);
        var iBr2 = new SolidBrush(Color.FromArgb(160, 175, 200));
        var vvBr2 = new SolidBrush(Color.FromArgb(210, 220, 240));
        int rx = 238, ry = 12;
        g.DrawString("Fan", iF, iBr2, rx, ry);
        var fanClr = _isAutoMode ? Color.FromArgb(210, 220, 240) : Color.FromArgb(110, 190, 255);
        g.DrawString(_fanPct < 0 ? "--" : $"{_fanPct}%", iF, new SolidBrush(fanClr), rx + 42, ry);
        ry += 20;
        g.DrawString("RPM", iF, iBr2, rx, ry);
        g.DrawString(_rpm < 0 ? "--" : $"{_rpm:F0}", iF, vvBr2, rx + 42, ry);

        // Mode
        string mt = _isAutoMode ? "Auto ✓" : "Manual";
        using var mF = new Font("Segoe UI", 7.5f, _isAutoMode ? FontStyle.Regular : FontStyle.Bold);
        var mc = _isAutoMode ? Color.FromArgb(75, 200, 115) : Color.FromArgb(110, 175, 255);
        g.DrawString(mt, mF, new SolidBrush(mc), rx + 2, ry + 19);

        DrawPowerLimitButton(g, PowerLimit280Rect(), NvidiaPowerLimit.PowerLimit280W);
        DrawPowerLimitButton(g, PowerLimit350Rect(), NvidiaPowerLimit.PowerLimit350W);

        // Slider text
        string st = _sliderDragging ? $"{_sliderValue}%  {_rpm:F0} RPM" :
                    _isAutoMode ? "拖动滑块切换手动" : $"手动 {_sliderValue}%   按 Auto 切回";
        using var sF = new Font("Segoe UI", 7);
        g.DrawString(st, sF, new SolidBrush(Color.FromArgb(150, 160, 200)), Pad, _sliderY + SliderH + 4);

        // ═══ Alpha mask: set alpha=0 outside rounded rect, alpha=230 inside ═══
        using var maskBmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var mg = Graphics.FromImage(maskBmp);
        mg.SmoothingMode = SmoothingMode.HighQuality;
        mg.Clear(Color.Transparent);
        var maskPath = RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
        using (var wb = new SolidBrush(Color.White))
            mg.FillPath(wb, maskPath);

        var bmpData = bmp.LockBits(new Rectangle(0, 0, Width, Height),
            System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var maskData = maskBmp.LockBits(new Rectangle(0, 0, Width, Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* px = (byte*)bmpData.Scan0;
            byte* mx = (byte*)maskData.Scan0;
            int stride = bmpData.Stride;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = y * stride + x * 4;
                    byte maskAlpha = mx[i + 3]; // 255 inside, 0 outside, gradient at edges
                    if (maskAlpha == 0)
                    {
                        px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0; // fully transparent
                    }
                    else
                    {
                        // Scale alpha: 230/255 * maskAlpha for semi-transparent look
                        px[i + 3] = (byte)(230 * maskAlpha / 255);
                    }
                }
            }
        }
        bmp.UnlockBits(bmpData);
        maskBmp.UnlockBits(maskData);

        // Subtle border
        using var borderBmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var bg = Graphics.FromImage(borderBmp);
        bg.SmoothingMode = SmoothingMode.HighQuality;
        bg.Clear(Color.Transparent);
        using (var borderPen = new Pen(Color.FromArgb(35, 140, 140, 200), 1f))
            bg.DrawPath(borderPen, maskPath);

        // Merge border into main
        using var finalG = Graphics.FromImage(bmp);
        finalG.CompositingMode = CompositingMode.SourceOver;
        finalG.DrawImage(borderBmp, 0, 0);

        // Apply to layered window
        ApplyBitmap(bmp);
    }

    private void ApplyBitmap(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var oldBmp = SelectObject(memDc, hBitmap);
        var ptDst = new POINT { x = Left, y = Top };
        var sz = new SIZE { cx = Width, cy = Height };
        var ptSrc = new POINT { x = 0, y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref sz, memDc, ref ptSrc, 0, ref blend, 2);
        SelectObject(memDc, oldBmp);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static GraphicsPath RoundedRect(Rectangle b, int r)
    {
        var p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(b.X, b.Y, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ════════════════════════════════════════════════════════════
//  GPU Sensor Reader (LibreHardwareMonitor)
// ════════════════════════════════════════════════════════════
static class GpuSensorReader
{
    private static readonly object Gate = new();
    private static Computer? _computer;

    public static double ReadGpuVramTemp() =>
        ReadTemperature("GPU Memory Junction", "Memory Junction");

    public static double ReadGpuCoreTemp() =>
        ReadTemperature("GPU Core", "GPU Temperature");

    public static double ReadGpuPower() =>
        ReadSensor(SensorType.Power, "GPU Package", "GPU Board Power", "GPU Power");

    public static double ReadGpuCoreClock() =>
        ReadSensor(SensorType.Clock, "GPU Core");

    private static double ReadTemperature(params string[] nameNeedles)
    {
        return ReadSensor(SensorType.Temperature, nameNeedles);
    }

    private static double ReadSensor(SensorType sensorType, params string[] nameNeedles)
    {
        try
        {
            lock (Gate)
            {
                EnsureOpen();
                if (_computer == null) return -999;

                foreach (var hardware in _computer.Hardware)
                {
                    var value = ReadFromHardware(hardware, sensorType, nameNeedles);
                    if (value >= 0) return value;

                    foreach (var subHardware in hardware.SubHardware)
                    {
                        value = ReadFromHardware(subHardware, sensorType, nameNeedles);
                        if (value >= 0) return value;
                    }
                }
            }
            return -1;
        }
        catch
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            return -999;
        }
    }

    private static void EnsureOpen()
    {
        if (_computer != null) return;
        _computer = new Computer { IsGpuEnabled = true };
        _computer.Open();
    }

    private static double ReadFromHardware(IHardware hardware, SensorType sensorType, string[] nameNeedles)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != sensorType || sensor.Value == null) continue;
            foreach (var needle in nameNeedles)
            {
                if (sensor.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sensor.Value.Value;
            }
        }
        return -1;
    }
}

static class NvidiaPowerLimit
{
    public const int PowerLimit280W = 280;
    public const int PowerLimit350W = 350;

    public static double GetPowerLimit()
    {
        var (exitCode, stdout, _) = RunNvidiaSmi("--query-gpu=power.limit --format=csv,noheader,nounits");
        if (exitCode != 0) return -1;

        var first = stdout.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts)
            ? watts
            : -1;
    }

    public static bool SetPowerLimit(int watts)
    {
        var (exitCode, _, _) = RunNvidiaSmi($"-pl {watts}");
        return exitCode == 0;
    }

    private static (int exitCode, string stdout, string stderr) RunNvidiaSmi(string arguments)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            proc.Start();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                return (-1, "", "nvidia-smi timed out");
            }
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}

// ════════════════════════════════════════════════════════════
//  Fan Controller (LibreHardwareMonitor → NCT6687D PUMP_FAN1)
// ════════════════════════════════════════════════════════════
class FanController : IDisposable
{
    private Computer? _computer;
    private IHardware? _superio;
    private ISensor? _pumpControl;
    private ISensor? _pumpFanRpm;

    public bool IsReady => _pumpControl != null;

    public bool Initialize()
    {
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fan-init.log");
        try
        {
            File.WriteAllText(logPath, $"[{DateTime.Now}] Init\n");
            _computer = new Computer { IsMotherboardEnabled = true };
            _computer.Open();

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                Scan(hw, logPath);
                foreach (var sub in hw.SubHardware) { sub.Update(); Scan(sub, logPath); }
            }

            File.AppendAllText(logPath, $"Pump: {_pumpControl?.Identifier} RPM: {_pumpFanRpm?.Identifier} Ready: {IsReady}\n");
            return IsReady;
        }
        catch (Exception ex) { File.AppendAllText(logPath, $"ERR: {ex}\n"); return false; }
    }

    private void Scan(IHardware hw, string log)
    {
        if (hw.HardwareType != HardwareType.SuperIO) return;
        _superio = hw;
        foreach (var s in hw.Sensors)
        {
            var id = s.Identifier.ToString();
            if (s.SensorType == SensorType.Control || s.SensorType == SensorType.Fan)
                File.AppendAllText(log, $"  {s.SensorType}: '{s.Name}' = {s.Value} ({id})\n");
            if (s.SensorType == SensorType.Control && id.EndsWith("control/1")) _pumpControl = s;
            if (s.SensorType == SensorType.Fan && id.EndsWith("fan/1")) _pumpFanRpm = s;
        }
    }

    public void SetPumpFanSpeed(int pct)
    {
        if (_pumpControl?.Control == null) return;
        _pumpControl.Control.SetSoftware(Math.Clamp(pct, 0, 100));
        _superio?.Update();
    }

    public double GetPumpRpm()
    {
        _superio?.Update();
        return _pumpFanRpm?.Value ?? -1;
    }

    public void Dispose()
    {
        if (_pumpControl?.Control != null) { _pumpControl.Control.SetDefault(); _superio?.Update(); }
        _computer?.Close();
    }
}
