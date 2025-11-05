// Program.cs - ChatMouse (single-instance, tray, hotkey, FlaUI selection, IPC with WM_COPYDATA)
// Hotkey focus stability:
//  - Capture foreground HWND at hotkey moment and pass it down.
//  - Wait modifiers to be released.
//  - Optionally SetForegroundWindow(savedHwnd) before Ctrl+C probe.
//  - Hidden hotkey window uses WS_EX_NOACTIVATE so focus is not stolen.

#region Using

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using Timer = System.Windows.Forms.Timer;
using WinFormsApp = System.Windows.Forms.Application;

#endregion

#region Models

public class ChatMessage { public string role { get; set; } = ""; public string content { get; set; } = ""; }
public class ChatRequest { public string model { get; set; } = ""; public ChatMessage[] messages { get; set; } = Array.Empty<ChatMessage>(); public double temperature { get; set; } = 0.7; }
public class ChatChoice { public ChatMessage message { get; set; } = new ChatMessage(); }
public class ChatResponse { public ChatChoice[] choices { get; set; } = Array.Empty<ChatChoice>(); }

#endregion

#region UI Constants

static class Ui
{
    public const int Corner = 12;
    public static readonly Padding Pad = new Padding(16, 12, 16, 14);
    public const int MaxWidth = 560;
    public const float TargetOpacity = 0.97f;
    public static readonly Font Font = new Font("Segoe UI", 11f, FontStyle.Regular);
    public static readonly Color BgTop = Color.FromArgb(242, 30, 30, 30);
    public static readonly Color BgBottom = Color.FromArgb(235, 24, 24, 24);
    public static readonly Color Border = Color.FromArgb(100, 255, 255, 255);
    public static readonly Color Text = Color.White;
}

#endregion

#region Logger

static class Logger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string _path = Path.Combine(AppContext.BaseDirectory, "ChatMouse.log");

    public static void Init()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(AppContext.BaseDirectory);
                _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                Info("===== App Start =====");
                Info($".NET: {Environment.Version}, OS: {Environment.OSVersion}");
            }
        }
        catch { }
    }
    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(Exception ex, string? msg = null) =>
        Write("ERROR", (msg == null ? "" : msg + " - ") + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);

    private static void Write(string level, string msg)
    {
        try { lock (_lock) { if (_writer == null) return; _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}"); } }
        catch { }
    }
    public static void Close()
    {
        try { lock (_lock) { if (_writer != null) { Info("===== App Exit ====="); _writer.Flush(); _writer.Dispose(); _writer = null; } } } catch { }
    }
}

#endregion

#region Tooltip Form

public class PrettyTooltipForm : Form
{
    private string _text;
    private readonly Timer _animTimer = new() { Interval = 16 };
    private readonly Timer _cursorWatch = new() { Interval = 30 };

    private enum AnimMode { None, FadeIn, FadeOut }
    private AnimMode _mode = AnimMode.None;
    private DateTime _animStart;
    private const int FadeInMs = 220;
    private const int FadeOutMs = 180;

    private Point _anchorCursor;
    private const int JitterPx = 4;
    private const int SpawnOffset = 12;

    private readonly Stopwatch _life = new();
    private const int GraceMs = 3000;

    public PrettyTooltipForm(string initialText)
    {
        _text = initialText ?? "";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Opacity = 0.0;
        BackColor = Color.Lime;
        TransparencyKey = Color.Lime;

        _animTimer.Tick += (s, e) =>
        {
            int dur = _mode == AnimMode.FadeIn ? FadeInMs : FadeOutMs;
            double t = (DateTime.Now - _animStart).TotalMilliseconds / dur;
            t = Math.Clamp(t, 0, 1);
            double eased = EaseOutCubic(t);
            if (_mode == AnimMode.FadeIn) Opacity = Ui.TargetOpacity * eased;
            else if (_mode == AnimMode.FadeOut) Opacity = Ui.TargetOpacity * (1 - eased);
            if (t >= 1) { _animTimer.Stop(); if (_mode == AnimMode.FadeOut) Close(); _mode = AnimMode.None; }
        };

        _cursorWatch.Tick += (s, e) =>
        {
            if (_life.ElapsedMilliseconds < GraceMs) return;
            var cur = Cursor.Position;
            if (Math.Abs(cur.X - _anchorCursor.X) > JitterPx || Math.Abs(cur.Y - _anchorCursor.Y) > JitterPx)
            { _cursorWatch.Stop(); BeginFadeOut(); }
        };

        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) BeginFadeOut(); };

        _anchorCursor = Cursor.Position;
        RecalcSizeAndPlaceNearCursor(_anchorCursor);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
    protected override void OnShown(EventArgs e)
    { base.OnShown(e); BeginFadeIn(); _life.Restart(); _cursorWatch.Start(); Logger.Info("Tooltip shown"); }

    public void SetText(string t) { _text = t ?? ""; RecalcSizeAndPlaceNearCursor(_anchorCursor); Invalidate(); Logger.Info($"Tooltip text set (len={_text.Length})"); }
    public void BeginFadeIn() { _mode = AnimMode.FadeIn; _animStart = DateTime.Now; Opacity = 0.0; _animTimer.Start(); }
    public void BeginFadeOut() { if (_mode == AnimMode.FadeOut) return; _mode = AnimMode.FadeOut; _animStart = DateTime.Now; _animTimer.Start(); Logger.Info("Tooltip fade-out started"); }

    private static double EaseOutCubic(double t) { t = 1 - t; return 1 - t * t * t; }

    private void RecalcSizeAndPlaceNearCursor(Point anchor)
    {
        using (Graphics g = CreateGraphics())
        {
            Size proposed = new(Ui.MaxWidth, int.MaxValue);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            Size measured = TextRenderer.MeasureText(g, string.IsNullOrEmpty(_text) ? " " : _text, Ui.Font, proposed, flags);
            int w = Math.Max(240, measured.Width + Ui.Pad.Horizontal + 6);
            int h = Math.Max(96, measured.Height + Ui.Pad.Vertical + 6);
            Size = new Size(w, h);

            using (GraphicsPath gp = RoundedRect(new Rectangle(0, 0, w, h), Ui.Corner)) Region = new Region(gp);

            Rectangle workArea = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X + SpawnOffset, y = anchor.Y + SpawnOffset;
            if (x + w > workArea.Right) x = workArea.Right - w - SpawnOffset;
            if (y + h > workArea.Bottom) y = workArea.Bottom - h - SpawnOffset;
            if (x < workArea.Left) x = workArea.Left + SpawnOffset;
            if (y < workArea.Top) y = workArea.Top + SpawnOffset;
            Location = new Point(x, y);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var pathShadow1 = RoundedRect(new Rectangle(4, 6, rect.Width, rect.Height), Ui.Corner + 3))
        using (var shadow1 = new SolidBrush(Color.FromArgb(34, 0, 0, 0))) g.FillPath(shadow1, pathShadow1);
        using (var pathShadow2 = RoundedRect(new Rectangle(2, 3, rect.Width, rect.Height), Ui.Corner + 1))
        using (var shadow2 = new SolidBrush(Color.FromArgb(18, 0, 0, 0))) g.FillPath(shadow2, pathShadow2);

        using var bgPath = RoundedRect(rect, Ui.Corner);
        using var lg = new LinearGradientBrush(rect, Ui.BgTop, Ui.BgBottom, 90f);
        g.FillPath(lg, bgPath);
        using var border = new Pen(Ui.Border, 1f); g.DrawPath(border, bgPath);

        var textRect = new Rectangle(Ui.Pad.Left, Ui.Pad.Top, Width - Ui.Pad.Horizontal, Height - Ui.Pad.Vertical);
        TextRenderer.DrawText(g, _text, Ui.Font, textRect, Ui.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var gp = new GraphicsPath();
        gp.StartFigure();
        gp.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        gp.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        gp.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        gp.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }
}

#endregion

#region Config

public class AppConfig
{
    public string base_url { get; set; } = "";
    public string api_key { get; set; } = "";
    public string model { get; set; } = "";
    public string prompt { get; set; } = "";
    public bool allow_clipboard_probe { get; set; } = true;

    public string? http_proxy { get; set; }
    public bool disable_ssl_verify { get; set; } = false;

    public bool tray_mode { get; set; } = false;
    public string hotkey { get; set; } = "Ctrl+Shift+Space";
}

#endregion

#region Program

public static class App
{
    private const string MutexName = "Global\\ChatMouse_Mutex_v1";
    private const string IpcWindowTitle = "ChatMouse_IPC_v1";
    private const int WM_COPYDATA = 0x004A;

    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static IpcWindow? _ipcWindow;
    private static HttpClient? _httpGlobal;

    [STAThread]
    public static void Main()
    {
        Logger.Init();
        Mutex? mutex = null;
        try
        {
            bool isOwner;
            mutex = new Mutex(true, MutexName, out isOwner);
            if (!isOwner)
            {
                Logger.Info("Secondary instance detected. Capturing context to send via IPC...");
                string context = CaptureContextForIpcFallbackSafe();
                if (!NotifyExistingInstance(context))
                {
                    Logger.Warn("Failed to notify primary instance.");
                    MessageBox.Show("이미 실행 중입니다.", "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            WinFormsApp.EnableVisualStyles();
            WinFormsApp.SetCompatibleTextRenderingDefault(false);

            WinFormsApp.ThreadException += (s, e) =>
            {
                Logger.Error(e.Exception, "UI ThreadException");
                MessageBox.Show("Unexpected error (UI): " + e.Exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex) Logger.Error(ex, "UnhandledException");
                else Logger.Error("UnhandledException: " + e.ExceptionObject?.ToString());
            };

            var cfg = LoadConfig();
            Logger.Info($"Config loaded. tray_mode={cfg.tray_mode}, hotkey={cfg.hotkey}, base_url={cfg.base_url}, model={cfg.model}, proxy={(cfg.http_proxy ?? "null")}, ssl_off={cfg.disable_ssl_verify}");

            _httpGlobal = CreateHttp(cfg);

            _ipcWindow = new IpcWindow();
            _ipcWindow.ReceivedPayload += async (_, payload) =>
            {
                try
                {
                    Logger.Info($"IPC payload received len={payload?.Length ?? 0}");
                    using var cts = new CancellationTokenSource();
                    await TriggerOnceAsync(cfg, _httpGlobal!, cts.Token, payload ?? string.Empty, IntPtr.Zero);
                }
                catch (Exception ex) { Logger.Error(ex, "IPC Trigger failed"); }
            };

            if (cfg.tray_mode)
            {
                Logger.Info("Entering tray mode...");
                WinFormsApp.Run(new TrayContext(cfg, _httpGlobal!));
            }
            else
            {
                Logger.Info("One-shot mode...");
                _ = TriggerOnceAsync(cfg, _httpGlobal!, CancellationToken.None, null, IntPtr.Zero);
                WinFormsApp.Run();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Main crashed");
            MessageBox.Show("Fatal error: " + ex.Message, "Pretty Tooltip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { _ipcWindow?.Destroy(); } catch { }
            try { _httpGlobal?.Dispose(); } catch { }
            try { mutex?.ReleaseMutex(); } catch { }
            Logger.Close();
        }
    }

    #region IPC

    private static bool NotifyExistingInstance(string? context)
    {
        IntPtr hwnd = FindWindow(null, IpcWindowTitle);
        if (hwnd == IntPtr.Zero) { Logger.Warn("NotifyExistingInstance: primary window not found"); return false; }

        string msg = context ?? string.Empty;
        byte[] bytes = Encoding.UTF8.GetBytes(msg);
        var cds = new COPYDATASTRUCT { dwData = IntPtr.Zero, cbData = bytes.Length, lpData = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, cds.lpData, bytes.Length);
        try { SendMessage(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds); Logger.Info("WM_COPYDATA sent."); return true; }
        catch (Exception ex) { Logger.Error(ex, "NotifyExistingInstance failed"); return false; }
        finally { Marshal.FreeHGlobal(cds.lpData); }
    }

    private sealed class IpcWindow : NativeWindow
    {
        public event EventHandler<string?>? ReceivedPayload;
        public IpcWindow()
        {
            var cp = new CreateParams
            {
                Caption = IpcWindowTitle, X = 0, Y = 0, Height = 0, Width = 0,
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = 0x80 | 0x00000008        // WS_EX_TOOLWINDOW | WS_EX_TOPMOST
            };
            CreateHandle(cp);
        }
        public void Destroy() { try { DestroyHandle(); } catch { } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA)
            {
                try
                {
                    var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(m.LParam);
                    string? payload = null;
                    if (cds.cbData > 0 && cds.lpData != IntPtr.Zero)
                    {
                        byte[] data = new byte[cds.cbData];
                        Marshal.Copy(cds.lpData, data, 0, cds.cbData);
                        payload = Encoding.UTF8.GetString(data);
                    }
                    ReceivedPayload?.Invoke(this, payload);
                    m.Result = new IntPtr(1);
                    return;
                }
                catch (Exception ex) { Logger.Error(ex, "IPC Receive error"); m.Result = IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    #endregion

    #region Tray + Hotkey

    private sealed class TrayContext : ApplicationContext
    {
        private readonly AppConfig _cfg;
        private readonly HttpClient _http;
        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hotkeyWnd;

        public TrayContext(AppConfig cfg, HttpClient http)
        {
            Logger.Info("TrayContext ctor");
            _cfg = cfg; _http = http;

            var menu = new ContextMenuStrip();
            var miShow = new ToolStripMenuItem("Show ( " + (_cfg.hotkey) + " )");
            miShow.Click += (_, __) => TriggerWithHwnd(IntPtr.Zero);
            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += (_, __) => ExitThread();

            menu.Items.Add(miShow); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(miExit);

            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(WinFormsApp.ExecutablePath) ?? SystemIcons.Application,
                Text = "ChatMouse", Visible = true, ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, __) => TriggerWithHwnd(IntPtr.Zero);

            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(icoPath)) { _tray.Icon = new Icon(icoPath); Logger.Info("Custom tray icon loaded"); }
            }
            catch (Exception ex) { Logger.Warn("Tray icon load failed: " + ex.Message); }

            _hotkeyWnd = new HotkeyWindow();
            if (!TryRegisterHotkey(_hotkeyWnd, _cfg.hotkey))
            {
                Logger.Warn($"Hotkey '{_cfg.hotkey}' register failed");
                _tray.ShowBalloonTip(3000, "ChatMouse", $"Hotkey '{_cfg.hotkey}' 등록 실패. 트레이 메뉴로 호출하세요.", ToolTipIcon.Warning);
            }
            else
            {
                Logger.Info($"Hotkey '{_cfg.hotkey}' registered");
                _tray.ShowBalloonTip(1200, "ChatMouse", $"Tray & Hotkey ready ({_cfg.hotkey})", ToolTipIcon.Info);
            }

            _hotkeyWnd.HotkeyPressed += (_, hwndAtPress) => TriggerWithHwnd(hwndAtPress);

            // first-run: show once
            var oneShot = new Timer { Interval = 150 };
            oneShot.Tick += (_, __) =>
            {
                try { oneShot.Stop(); oneShot.Dispose(); } catch { }
                TriggerWithHwnd(GetForegroundWindow());
            };
            oneShot.Start();
        }

        private void TriggerWithHwnd(IntPtr hwndAtPress)
        {
            Logger.Info($"Trigger from tray/hotkey (hwnd=0x{hwndAtPress.ToInt64():X})");
            _ = TriggerOnceAsync(_cfg, _http, CancellationToken.None, null, hwndAtPress);
        }

        protected override void ExitThreadCore()
        {
            Logger.Info("TrayContext Exit");
            try { _hotkeyWnd.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            base.ExitThreadCore();
        }
    }

    private class HotkeyWindow : NativeWindow, IDisposable
    {
        public event EventHandler<IntPtr>? HotkeyPressed;
        private const int WM_HOTKEY = 0x0312;

        public HotkeyWindow()
        {
            var cp = new CreateParams
            {
                Caption = "ChatMouse_HotkeySink",
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = 0x80 | 0x08000000        // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                // Capture current foreground hwnd *before* we do anything else
                IntPtr hwnd = GetForegroundWindow();
                HotkeyPressed?.Invoke(this, hwnd);
            }
            base.WndProc(ref m);
        }

        public void Dispose() { try { DestroyHandle(); } catch { } }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private static readonly int HotkeyId = 0xB00B;

    private static bool TryRegisterHotkey(HotkeyWindow wnd, string hotkey)
    {
        ParseHotkey(hotkey, out uint mods, out uint key);
        _ = UnregisterHotKey(wnd.Handle, HotkeyId);
        return RegisterHotKey(wnd.Handle, HotkeyId, mods, key);
    }

    private static void ParseHotkey(string s, out uint mods, out uint key)
    {
        mods = 0; key = 0;
        var parts = (s ?? "Ctrl+Shift+Space").Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            var p = raw.Trim().ToUpperInvariant();
            switch (p)
            {
                case "CTRL":
                case "CONTROL": mods |= MOD_CONTROL; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "ALT": mods |= MOD_ALT; break;
                case "WIN":
                case "WINDOWS": mods |= MOD_WIN; break;
                default: key = NameToVk(p); break;
            }
        }
        if (key == 0) key = (uint)Keys.Space;
    }

    private static uint NameToVk(string p)
    {
        if (Enum.TryParse<Keys>(p, true, out var k)) return (uint)k;
        return p.ToUpperInvariant() switch
        {
            "ESC" or "ESCAPE" => (uint)Keys.Escape,
            "RET" or "ENTER" => (uint)Keys.Return,
            "BKSP" or "BACKSPACE" => (uint)Keys.Back,
            "DEL" or "DELETE" => (uint)Keys.Delete,
            "TAB" => (uint)Keys.Tab,
            "SPACE" => (uint)Keys.Space,
            _ => (uint)Keys.Space
        };
    }

    #endregion

    #region Trigger Once

    private static async Task TriggerOnceAsync(AppConfig cfg, HttpClient http, CancellationToken externalCt, string? presetContextOrNull, IntPtr hwndAtPress)
    {
        await Task.Yield();
        Logger.Info($"TriggerOnceAsync begin (hwndAtPress=0x{hwndAtPress.ToInt64():X})");

        // Wait until modifiers are released (prevents interfering with Ctrl+C probe etc.)
        await WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(250));

        var tooltip = new PrettyTooltipForm("⏳ 선택 텍스트 확인 중…");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        tooltip.FormClosed += (_, __) => { try { cts.Cancel(); cts.Dispose(); } catch { } };

        var anchor = Cursor.Position;

        DateTime start = DateTime.Now;
        var cancelWatch = new Timer { Interval = 30 };
        cancelWatch.Tick += (s, e) =>
        {
            if (!tooltip.Visible) { cancelWatch.Stop(); return; }
            if ((DateTime.Now - start).TotalMilliseconds < 3000) return;
            var cur = Cursor.Position;
            if (Math.Abs(cur.X - anchor.X) > 4 || Math.Abs(cur.Y - anchor.Y) > 4)
            {
                Logger.Info("Mouse moved → cancel tooltip");
                cancelWatch.Stop();
                try { cts.Cancel(); } catch { }
                tooltip.BeginFadeOut();
            }
        };
        cancelWatch.Start();

        tooltip.Shown += async (_, __) =>
        {
            try
            {
                string? context = presetContextOrNull;
                if (string.IsNullOrWhiteSpace(context))
                    context = await GetContextTextPreferSelectionAsync(cfg, cts.Token, hwndAtPress);

                Logger.Info("Context captured: " + (context == null ? "null" : $"len={context.Length}"));
                if (string.IsNullOrWhiteSpace(context)) { tooltip.SetText("📌 선택 텍스트/클립보드 모두 찾지 못했습니다."); return; }

                tooltip.SetText("⏳ LLM에 요청 중…");
                string answer = await QueryLLMAsync(http, cfg, context, cts.Token);
                Logger.Info("LLM response len=" + (answer?.Length ?? 0));
                tooltip.SetText(string.IsNullOrEmpty(answer) ? "(빈 응답)" : answer);
            }
            catch (OperationCanceledException)
            { Logger.Info("Trigger canceled (silent)."); tooltip.BeginFadeOut(); return; }
            catch (ObjectDisposedException ode)
            { Logger.Warn("Token disposed during Shown handler: " + ode.Message); tooltip.BeginFadeOut(); return; }
            catch (Exception ex)
            { Logger.Error(ex, "TriggerOnceAsync error"); tooltip.SetText($"❌ Error: {ex.Message}"); }
        };

        tooltip.Show();
        Logger.Info("Tooltip.Show called");
    }

    #endregion

    #region HTTP / Config

    private static HttpClient CreateHttp(AppConfig cfg)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(cfg.http_proxy)) { handler.Proxy = new WebProxy(cfg.http_proxy); handler.UseProxy = true; Logger.Info($"Proxy enabled: {cfg.http_proxy}"); }
        if (cfg.disable_ssl_verify) { handler.ServerCertificateCustomValidationCallback = static (_, __, ___, ____) => true; Logger.Warn("SSL verification disabled"); }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static AppConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            var sample = new AppConfig
            {
                base_url = "http://127.0.0.1:8000/v1",
                api_key = "EMPTY",
                model = "my-vllm-model",
                prompt = "다음 텍스트를 요약해줘:",
                allow_clipboard_probe = true,
                http_proxy = null,
                disable_ssl_verify = false,
                tray_mode = true,
                hotkey = "Ctrl+Shift+Space"
            };
            string json = JsonSerializer.Serialize(sample, JsonOpts);
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
            Logger.Info("config.json created with defaults");
            return sample;
        }

        string raw = File.ReadAllText(ConfigPath, Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw, JsonOpts) ?? new AppConfig();

        cfg.base_url ??= "http://127.0.0.1:8000/v1";
        cfg.api_key ??= "EMPTY";
        cfg.model ??= "my-vllm-model";
        cfg.prompt ??= "다음 텍스트를 요약해줘:";
        if (string.IsNullOrWhiteSpace(cfg.hotkey)) cfg.hotkey = "Ctrl+Shift+Space";
        if (!raw.Contains("\"tray_mode\"")) cfg.tray_mode = true;

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts), new UTF8Encoding(false));
        return cfg;
    }

    #endregion

    #region LLM

    private static async Task<string> QueryLLMAsync(HttpClient http, AppConfig cfg, string userText, CancellationToken ct)
    {
        string baseUrl = (cfg.base_url ?? "").TrimEnd('/');
        string endpoint = $"{baseUrl}/chat/completions";

        var requestObj = new ChatRequest
        {
            model = cfg.model ?? "",
            messages = new[]
            {
                new ChatMessage { role = "system", content = cfg.prompt ?? "" },
                new ChatMessage { role = "user", content = userText }
            }
        };

        string json = JsonSerializer.Serialize(requestObj, JsonOpts);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        if (!string.Equals(cfg.api_key, "EMPTY", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cfg.api_key))
            httpReq.Headers.Add("Authorization", $"Bearer {cfg.api_key}");

        try
        {
            Logger.Info("HTTP POST " + endpoint);
            var resp = await http.SendAsync(httpReq, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Logger.Info($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}, {body.Length} bytes");

            if (!resp.IsSuccessStatusCode) return $"❌ Request failed ({(int)resp.StatusCode}): {resp.ReasonPhrase}";

            try
            {
                var chatResp = JsonSerializer.Deserialize<ChatResponse>(body, JsonOpts);
                string? content = chatResp?.choices?.Length > 0 ? chatResp!.choices[0].message?.content : null;
                return string.IsNullOrWhiteSpace(content) ? "(no response)" : content!.Trim();
            }
            catch (Exception ex) { Logger.Error(ex, "Parse LLM JSON failed"); return "⚠️ Failed to parse JSON response"; }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { Logger.Info("HTTP canceled"); throw; }
        catch (Exception ex) { Logger.Error(ex, "HTTP error"); return $"❌ Network error: {ex.Message}"; }
    }

    #endregion

    #region Context (Selection → Win32 → Ctrl+C → Clipboard)

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int SendMessage(IntPtr hWnd, uint msg, int wParam, StringBuilder lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, out int wParam, out int lParam);

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETSEL = 0x00B0;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    // SendInput
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg, wParamL, wParamH; }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_C = 0x43;

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);

    private static async Task WaitForModifiersReleasedAsync(TimeSpan maxWait)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < maxWait)
        {
            bool down =
                (GetKeyState(VK_CONTROL) < 0) ||
                (GetKeyState(VK_SHIFT) < 0) ||
                (GetKeyState(VK_MENU) < 0) ||
                (GetKeyState(VK_LWIN) < 0) ||
                (GetKeyState(VK_RWIN) < 0);
            if (!down) break;
            await Task.Delay(20);
        }
    }

    private static void SendCtrlC()
    {
        var inputs = new INPUT[4];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].U.ki = new KEYBDINPUT { wVk = VK_CONTROL };
        inputs[1].type = INPUT_KEYBOARD; inputs[1].U.ki = new KEYBDINPUT { wVk = VK_C };
        inputs[2].type = INPUT_KEYBOARD; inputs[2].U.ki = new KEYBDINPUT { wVk = VK_C, dwFlags = KEYEVENTF_KEYUP };
        inputs[3].type = INPUT_KEYBOARD; inputs[3].U.ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static async Task<string?> GetContextTextPreferSelectionAsync(AppConfig cfg, CancellationToken outerCt, IntPtr hwndAtPress)
    {
        // 0) If we got hwnd from hotkey moment, prefer using it
        IntPtr targetHwnd = hwndAtPress != IntPtr.Zero ? hwndAtPress : GetForegroundWindow();

        // 1) UIA3 selection via saved hwnd → focused element
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(900);
            string? uiTxt = await RunOnStaAsync<string?>(() =>
            {
                try
                {
                    using var automation = new UIA3Automation();
                    var element = automation.FocusedElement(); // After hotkey, focus may still be correct
                    if (element == null || targetHwnd != IntPtr.Zero)
                    {
                        // Try from saved hwnd
                        try
                        {
                            var fromHwnd = automation.FromHandle(targetHwnd);
                            if (fromHwnd != null) element = fromHwnd;
                        }
                        catch { }
                    }

                    if (element == null) return null;

                    ITextPattern? textPat = element.Patterns?.Text?.PatternOrDefault;
                    if (textPat != null)
                    {
                        var sel = textPat.GetSelection();
                        if (sel != null && sel.Length > 0)
                        {
                            string t = sel[0].GetText(int.MaxValue);
                            if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
                        }
                    }

                    IValuePattern? valPat = element.Patterns?.Value?.PatternOrDefault;
                    if (valPat != null)
                    {
                        string? v = valPat.Value;
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                }
                catch (Exception ex) { Logger.Warn("FlaUI selection failed: " + ex.Message); }
                return null;
            }, TimeSpan.FromMilliseconds(900), cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(uiTxt)) return uiTxt;
        }
        catch (OperationCanceledException) when (outerCt.IsCancellationRequested) { throw; }
        catch (Exception ex) { Logger.Warn("FlaUI stage exception: " + ex.Message); }

        // 2) Win32 Edit/RichEdit
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(700);
            string? win32 = await Task.Run(() =>
            {
                try
                {
                    IntPtr fg = targetHwnd != IntPtr.Zero ? targetHwnd : GetForegroundWindow();
                    if (fg == IntPtr.Zero) return null;

                    uint tid = GetWindowThreadProcessId(fg, out _);
                    GUITHREADINFO gti = new() { cbSize = Marshal.SizeOf<GUITHREADINFO>() };

                    if (!GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero) return null;

                    var sbCls = new StringBuilder(256);
                    GetClassName(gti.hwndFocus, sbCls, sbCls.Capacity);
                    string cls = sbCls.ToString();

                    if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                    {
                        SendMessage(gti.hwndFocus, EM_GETSEL, out int selStart, out int selEnd);
                        int len = (int)SendMessage(gti.hwndFocus, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                        if (len > 0 && selEnd > selStart && selStart >= 0 && selEnd <= len)
                        {
                            var sb = new StringBuilder(len + 1);
                            SendMessage(gti.hwndFocus, WM_GETTEXT, sb.Capacity, sb);
                            string full = sb.ToString();
                            return full.Substring(selStart, selEnd - selStart);
                        }
                    }
                }
                catch (Exception ex) { Logger.Warn("Win32 selection failed: " + ex.Message); }
                return null;
            }, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(win32)) return win32;
        }
        catch (OperationCanceledException) when (outerCt.IsCancellationRequested) { throw; }
        catch (Exception ex) { Logger.Warn("Win32 stage exception: " + ex.Message); }

        // 3) Ctrl+C Probe (restore focus to saved hwnd)
        if (cfg.allow_clipboard_probe)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(600);
                string? probed = await RunOnStaAsync<string?>(() =>
                {
                    string? original = null;
                    try { if (Clipboard.ContainsText()) original = Clipboard.GetText(TextDataFormat.UnicodeText); } catch { }

                    try
                    {
                        if (targetHwnd != IntPtr.Zero) SetForegroundWindow(targetHwnd);
                        Thread.Sleep(80); // allow activation

                        SendCtrlC();
                        Thread.Sleep(140); // let clipboard update
                        if (Clipboard.ContainsText())
                        {
                            string captured = Clipboard.GetText(TextDataFormat.UnicodeText);
                            if (!string.IsNullOrWhiteSpace(captured) && !string.Equals(captured, original, StringComparison.Ordinal))
                                return captured.Trim();
                        }
                    }
                    catch (Exception ex) { Logger.Warn("Ctrl+C probe failed: " + ex.Message); }
                    finally
                    {
                        try { if (original != null) Clipboard.SetText(original, TextDataFormat.UnicodeText); } catch { }
                    }
                    return null;
                }, TimeSpan.FromMilliseconds(600), cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(probed)) return probed;
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested) { throw; }
            catch (Exception ex) { Logger.Warn("Probe stage exception: " + ex.Message); }
        }

        // 4) Clipboard fallback
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(300);
            string? clip = await RunOnStaAsync<string?>(() =>
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string val = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
                catch (Exception ex) { Logger.Warn("Clipboard read failed: " + ex.Message); }
                return null;
            }, TimeSpan.FromMilliseconds(300), cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(clip)) return clip;
        }
        catch (OperationCanceledException) when (outerCt.IsCancellationRequested) { throw; }
        catch (Exception ex) { Logger.Warn("Clipboard stage exception: " + ex.Message); }

        return null;
    }

    private static string CaptureContextForIpcFallbackSafe()
    {
        try
        {
            string? clip = null;
            RunOnStaBlocking(() =>
            {
                try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(TextDataFormat.UnicodeText); } catch { }
            }, TimeSpan.FromMilliseconds(250));
            return clip?.Trim() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    #endregion

    #region STA helpers

    private static Task<T?> RunOnStaAsync<T>(Func<T?> func, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { tcs.TrySetResult(func()); } catch (Exception ex) { Logger.Error(ex, "RunOnStaAsync func error"); tcs.TrySetException(ex); } });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();

        return Task.Run(async () =>
        {
            using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeout, lcts.Token);
            var finished = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            if (finished == tcs.Task) { lcts.Cancel(); return await tcs.Task.ConfigureAwait(false); }
            Logger.Warn("RunOnStaAsync timeout"); return default;
        }, CancellationToken.None);
    }

    private static void RunOnStaBlocking(Action action, TimeSpan timeout)
    {
        Exception? captured = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } });
        t.IsBackground = true; t.SetApartmentState(ApartmentState.STA); t.Start();
        if (!t.Join(timeout)) Logger.Warn("RunOnStaBlocking timeout");
        if (captured != null) throw captured;
    }

    #endregion
}

#endregion
