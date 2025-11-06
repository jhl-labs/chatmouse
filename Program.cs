// Program.cs - ChatMouse (single-instance, tray, hotkey, FlaUI selection, IPC with WM_COPYDATA)
// Features:
//  - Tooltip with fade, selectable Text mode (+ close button)
//  - CRLF newline normalization for consistent wrapping
//  - Busy/Sticky: LLM 요청 중 자동 닫힘 방지 + 결과 표시 고정 시간
//  - Per-request timeout (config.request_timeout_seconds 사용) + 명확한 타임아웃 메시지
//  - Config in %USERPROFILE%\.chatmouse\config.json
//  - Multiple prompts (1..9) with INDIVIDUAL GLOBAL HOTKEYS per prompt (no in-tooltip keys)
//  - Settings UI to edit 1..9 prompts + their hotkeys (넉넉한 크기)
//  - Robust hotkey parsing (NumPad/locale symbols) + duplicate detection
//  - On settings change, fully unregister ALL known hotkey IDs before re-registering
//  - One toast summary after hotkey registration (not per hotkey)
//  - ✅ Update checker: GitHub releases on startup + tray menu "Check for updates…"
//
// Hotkey focus stability:
//  - Capture foreground HWND at hotkey moment and pass it down.
//  - Wait modifiers to be released.
//  - Optionally SetForegroundWindow(savedHwnd) before Ctrl+C probe.
//  - Hidden hotkey window uses WS_EX_NOACTIVATE so focus is not stolen.

#region Using

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public const int MaxWidth = 640; // 넉넉히
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

    private static string GetBaseDirectory()
    {
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppContext.BaseDirectory;
    }
    private static string _path = Path.Combine(GetBaseDirectory(), "ChatMouse.log");

    public static void Init()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(GetBaseDirectory());
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
    private string _textRaw;
    private string _text;
    private readonly Timer _animTimer = new() { Interval = 16 };
    private readonly Timer _cursorWatch = new() { Interval = 30 };
    private readonly TextBox _textBox;
    private readonly Button _btnClose;

    private enum AnimMode { None, FadeIn, FadeOut }
    private AnimMode _mode = AnimMode.None;
    private DateTime _animStart;
    private const int FadeInMs = 220;
    private const int FadeOutMs = 180;

    private Point _anchorCursor;
    private const int SpawnOffset = 12;

    private readonly Stopwatch _life = new();
    private const int GraceMs = 3000;
    private bool _isMouseOver = false;
    private bool _useTextBox = false;

    private DateTime _approachUntil;
    private bool _hasApproached = false;
    private DateTime? _awaySince = null;
    private const int MaxIdleMsWithoutApproach = 8000;

    // Busy/Sticky 상태
    private bool _busy = false;
    private DateTime _stickyUntil = DateTime.MinValue;

    public PrettyTooltipForm(string initialText)
    {
        _textRaw = initialText ?? "";
        _text = NormalizeNewlines(_textRaw);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 24);
        Opacity = 0.0;

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);
        UpdateStyles();

        _textBox = new TextBox
        {
            Text = _text,
            Font = Ui.Font,
            ForeColor = Ui.Text,
            BackColor = Color.FromArgb(30, 30, 30),
            BorderStyle = BorderStyle.None,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.None,
            TabStop = false,
            Visible = false,
            Enabled = false
        };
        Controls.Add(_textBox);
        Controls.SetChildIndex(_textBox, Controls.Count - 1);

        _btnClose = new Button
        {
            Text = "×",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(48, 48, 48),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(26, 26),
            TabStop = false,
            Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _btnClose.FlatAppearance.BorderSize = 0;
        _btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 70, 70);
        _btnClose.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 90);
        _btnClose.Click += (_, __) => BeginFadeOut();
        Controls.Add(_btnClose);

        MouseEnter += (s, e) => { _isMouseOver = true; _hasApproached = true; _cursorWatch.Stop(); };
        MouseLeave += (s, e) =>
        {
            _isMouseOver = false;
            if (_busy) return; // 요청 중에는 자동 닫힘 감시 중지
            if (_life.ElapsedMilliseconds >= GraceMs && !_useTextBox) _cursorWatch.Start();
        };
        MouseClick += (s, e) =>
        {
            if (!_useTextBox && _isMouseOver)
            {
                try { Activate(); } catch { }
                SwitchToTextBoxMode();
                try { _textBox.Focus(); _textBox.SelectAll(); } catch { }
            }
        };
        _textBox.MouseEnter += (s, e) => { _isMouseOver = true; _cursorWatch.Stop(); };
        _textBox.MouseLeave += (s, e) => { _isMouseOver = false; };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { BeginFadeOut(); return; }
            if (e.Control && e.KeyCode == Keys.W) { BeginFadeOut(); return; }
        };

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
            if (_busy) return; // 요청중이면 닫지 않음
            if (DateTime.Now < _stickyUntil) return; // 결과 고정 시간
            if (DateTime.Now < _approachUntil) return;

            if (!_hasApproached)
            {
                if (_life.ElapsedMilliseconds >= MaxIdleMsWithoutApproach)
                {
                    _cursorWatch.Stop(); BeginFadeOut();
                }
                return;
            }
            if (_isMouseOver) { _awaySince = null; return; }

            var near = this.Bounds; near.Inflate(48, 48);
            if (near.Contains(Cursor.Position)) { _hasApproached = true; _awaySince = null; return; }

            if (_awaySince == null) { _awaySince = DateTime.Now; return; }
            if ((DateTime.Now - _awaySince.Value).TotalMilliseconds >= 600)
            {
                _cursorWatch.Stop(); BeginFadeOut();
            }
        };

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
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RecalcSizeAndPlaceNearCursor(_anchorCursor);
        BeginFadeIn();
        _life.Restart();
        _approachUntil = DateTime.Now.AddMilliseconds(2000);
        _cursorWatch.Start();
        Logger.Info("Tooltip shown");
    }

    public void SetText(string t)
    {
        _textRaw = t ?? "";
        _text = NormalizeNewlines(_textRaw);
        _textBox.Text = _text;
        RecalcSizeAndPlaceNearCursor(_anchorCursor);
        Invalidate();
        Logger.Info($"Tooltip text set (len={_text.Length})");
    }

    public void SwitchToTextBoxMode()
    {
        if (_useTextBox) return;
        _useTextBox = true;
        _cursorWatch.Stop();

        _textBox.Text = _text;
        _textBox.Enabled = true;
        _textBox.Visible = true;
        Controls.SetChildIndex(_textBox, 0);

        _btnClose.Visible = true;

        LayoutInner();
        Invalidate();
        Logger.Info("Tooltip switched to TextBox mode for text selection");
    }

    private void LayoutInner()
    {
        _textBox.Size = new Size(Width - Ui.Pad.Horizontal, Height - Ui.Pad.Vertical);
        _textBox.Location = new Point(Ui.Pad.Left, Ui.Pad.Top);
        using var textBoxPath = new GraphicsPath();
        textBoxPath.AddRectangle(new Rectangle(0, 0, _textBox.Width, _textBox.Height));
        _textBox.Region = new Region(textBoxPath);

        _btnClose.Location = new Point(Width - _btnClose.Width - 8, 8);
        _btnClose.BringToFront();
    }

    public void BeginFadeIn()
    {
        _mode = AnimMode.FadeIn;
        _animStart = DateTime.Now;
        Opacity = 0.01;
        _animTimer.Start();
    }

    public void BeginFadeOut()
    {
        if (_mode == AnimMode.FadeOut) return;
        if (_busy) return; // 요청중엔 닫기 금지
        if (DateTime.Now < _stickyUntil) return; // 결과 고정 중엔 닫기 금지
        _mode = AnimMode.FadeOut;
        _animStart = DateTime.Now;
        _animTimer.Start();
        Logger.Info("Tooltip fade-out started");
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy)
        {
            _cursorWatch.Stop();
        }
        else
        {
            if (!_useTextBox) _cursorWatch.Start();
        }
    }

    /// <summary>지정 시간(ms) 동안 자동 닫힘을 막아 결과를 읽을 시간을 준다.</summary>
    public void StickFor(int milliseconds)
    {
        if (milliseconds <= 0) return;
        var until = DateTime.Now.AddMilliseconds(milliseconds);
        if (until > _stickyUntil) _stickyUntil = until;
    }

    private static double EaseOutCubic(double t) { t = 1 - t; return 1 - t * t * t; }

    private void RecalcSizeAndPlaceNearCursor(Point anchor)
    {
        Size size;
        if (IsHandleCreated && Visible)
        {
            using (Graphics g = CreateGraphics())
            {
                Size proposed = new(Ui.MaxWidth, int.MaxValue);
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
                Size measured = TextRenderer.MeasureText(g, string.IsNullOrEmpty(_text) ? " " : _text, Ui.Font, proposed, flags);
                size = new Size(Math.Max(360, measured.Width + Ui.Pad.Horizontal + 6),
                                Math.Max(160, measured.Height + Ui.Pad.Vertical + 6));
            }
        }
        else
        {
            Size proposed = new(Ui.MaxWidth, int.MaxValue);
            Size measured = TextRenderer.MeasureText(string.IsNullOrEmpty(_text) ? " " : _text, Ui.Font, proposed,
                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            size = new Size(Math.Max(360, measured.Width + Ui.Pad.Horizontal + 6),
                            Math.Max(160, measured.Height + Ui.Pad.Vertical + 6));
        }

        Size = size;

        using (var gp = new GraphicsPath())
        { gp.AddRectangle(new Rectangle(0, 0, size.Width, size.Height)); Region = new Region(gp); }

        Rectangle workArea = Screen.FromPoint(anchor).WorkingArea;
        int x = anchor.X + SpawnOffset, y = anchor.Y + SpawnOffset;
        if (x + size.Width > workArea.Right) x = workArea.Right - size.Width - SpawnOffset;
        if (y + size.Height > workArea.Bottom) y = workArea.Bottom - SpawnOffset;
        if (x < workArea.Left) x = workArea.Left + SpawnOffset;
        if (y < workArea.Top) y = workArea.Top + SpawnOffset;
        Location = new Point(x, y);

        LayoutInner();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var pathShadow1 = RoundedRect(new Rectangle(4, 6, rect.Width, rect.Height), Ui.Corner + 3))
        using (var shadow1 = new SolidBrush(Color.FromArgb(34, 0, 0, 0))) g.FillPath(shadow1, pathShadow1);
        using (var pathShadow2 = RoundedRect(new Rectangle(2, 3, rect.Width, rect.Height), Ui.Corner + 1))
        using (var shadow2 = new SolidBrush(Color.FromArgb(18, 0, 0, 0))) g.FillPath(shadow2, pathShadow2);

        using var bgPath = RoundedRect(rect, Ui.Corner);
        using var lg = new LinearGradientBrush(rect, Ui.BgTop, Ui.BgBottom, 90f);
        g.FillPath(lg, bgPath);
        using var border = new Pen(Ui.Border, 1f); g.DrawPath(border, bgPath);

        if (!_useTextBox)
        {
            var textRect = new Rectangle(Ui.Pad.Left, Ui.Pad.Top, Width - Ui.Pad.Horizontal, Height - Ui.Pad.Vertical);
            TextRenderer.DrawText(g, _text, Ui.Font, textRect, Ui.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var gp = new GraphicsPath();
        gp.StartFigure();
        gp.AddArc(bounds.X, bounds.Y, d, d, 180f, 90f);
        gp.AddArc(bounds.Right - d, bounds.Y, d, d, 270f, 90f);
        gp.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
        gp.AddArc(bounds.X, bounds.Bottom - d, d, d, 90f, 90f);
        gp.CloseFigure();
        return gp;
    }

    private static string NormalizeNewlines(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var t = s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        return t;
    }
}

#endregion

#region Config

public class AppConfig
{
    public string base_url { get; set; } = "";
    public string api_key { get; set; } = "";
    public string model { get; set; } = "";

    public string prompt { get; set; } = "";                // legacy 1st prompt
    public string[] prompts { get; set; } = Array.Empty<string>();
    public string[] prompt_hotkeys { get; set; } = Array.Empty<string>();

    public bool allow_clipboard_probe { get; set; } = true;
    public string? http_proxy { get; set; }
    public bool disable_ssl_verify { get; set; } = false;

    public bool tray_mode { get; set; } = false;
    public string hotkey { get; set; } = "Ctrl+Shift+Space";
    public int request_timeout_seconds { get; set; } = 10;

    // Update checker config
    public bool update_check_enabled { get; set; } = true;
    public string update_repo_owner { get; set; } = "qmoix";
    public string update_repo_name { get; set; } = "chatmouse";
    public string? last_notified_version { get; set; }
}

#endregion

#region Program

public static class App
{
    private const string MutexName = "Global\\ChatMouse_Mutex_v1";
    private const string IpcWindowTitle = "ChatMouse_IPC_v1";
    private const int WM_COPYDATA = 0x004A;

    private static AppConfig? _cfgCurrent;
    public static AppConfig GetCurrentConfig() => _cfgCurrent ?? new AppConfig();
    public static event Action<AppConfig>? ConfigChanged;
    private static FileSystemWatcher? _cfgWatcher;
    private static System.Threading.Timer? _cfgDebounce;

    // ~/.chatmouse/config.json
    private static string GetConfigDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(home, ".chatmouse");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
    private static readonly string ConfigPath = Path.Combine(GetConfigDirectory(), "config.json");

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
            _cfgCurrent = cfg;
            Logger.Info($"Config loaded. tray_mode={cfg.tray_mode}, hotkey={cfg.hotkey}, base_url={cfg.base_url}, model={cfg.model}, proxy={(cfg.http_proxy ?? "null")}, ssl_off={cfg.disable_ssl_verify}");

            _httpGlobal = CreateHttp(cfg);
            StartConfigWatcher();

            _ipcWindow = new IpcWindow();
            _ipcWindow.ReceivedPayload += async (_, payload) =>
            {
                try
                {
                    Logger.Info($"IPC payload received len={payload?.Length ?? 0}");
                    using var cts = new CancellationTokenSource();
                    var liveCfg = GetCurrentConfig();
                    await TriggerOnceAsync(liveCfg, _httpGlobal!, cts.Token, payload ?? string.Empty, IntPtr.Zero, forcedPrompt: null);
                }
                catch (Exception ex) { Logger.Error(ex, "IPC Trigger failed"); }
            };

            // 🔔 Check update on startup (fire-and-forget)
            if (cfg.update_check_enabled)
            {
                _ = Task.Run(async () =>
                {
                    try { await MaybeNotifyUpdateOnStartupAsync(cfg, _httpGlobal!); }
                    catch (Exception ex) { Logger.Warn("Startup update check failed: " + ex.Message); }
                });
            }

            if (cfg.tray_mode)
            {
                Logger.Info("Entering tray mode...");
                WinFormsApp.Run(new TrayContext(cfg, _httpGlobal!));
            }
            else
            {
                Logger.Info("One-shot mode...");
                EventHandler? idleHandler = null;
                idleHandler = (s, e) =>
                {
                    WinFormsApp.Idle -= idleHandler!;
                    _ = TriggerOnceAsync(GetCurrentConfig(), _httpGlobal!, CancellationToken.None, null, IntPtr.Zero, forcedPrompt: null);
                };
                WinFormsApp.Idle += idleHandler;
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

    public sealed class HotkeyEventArgs : EventArgs
    {
        public IntPtr HwndAtPress { get; }
        public int Id { get; }
        public HotkeyEventArgs(IntPtr hwnd, int id) { HwndAtPress = hwnd; Id = id; }
    }

    private sealed class TrayContext : ApplicationContext
    {
        private AppConfig _cfg;
        private HttpClient _http;
        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hotkeyWnd;
        private readonly SynchronizationContext _uiCtx;

        private ToolStripMenuItem? _miShow;

        private readonly HashSet<int> _registeredIds = new();
        private readonly HashSet<string> _normalizedCombos = new(StringComparer.OrdinalIgnoreCase);

        public TrayContext(AppConfig cfg, HttpClient http)
        {
            Logger.Info("TrayContext ctor");
            _cfg = cfg; _http = http;
            _uiCtx = SynchronizationContext.Current ?? new SynchronizationContext();

            var menu = new ContextMenuStrip();
            _miShow = new ToolStripMenuItem("Show ( " + (_cfg.hotkey) + " )");
            _miShow.Click += (_, __) => TriggerWithHwnd(IntPtr.Zero, forcedPrompt: null);

            var miCheckUpdate = new ToolStripMenuItem("Check for updates…");
            miCheckUpdate.Click += async (_, __) =>
            {
                try { await ManualCheckUpdateAsync(_cfg, _http); }
                catch (Exception ex) { Logger.Warn("Manual update check failed: " + ex.Message); MessageBox.Show("Update check failed: " + ex.Message, "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };

            var miSettings = new ToolStripMenuItem("Settings…");
            miSettings.Click += (_, __) => ShowSettingsDialog();

            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += (_, __) => ExitThread();

            menu.Items.Add(_miShow);
            menu.Items.Add(miCheckUpdate);
            menu.Items.Add(miSettings);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(WinFormsApp.ExecutablePath) ?? SystemIcons.Application,
                Text = "ChatMouse", Visible = true, ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, __) => TriggerWithHwnd(IntPtr.Zero, forcedPrompt: null);

            _hotkeyWnd = new HotkeyWindow();

            RegisterAllHotkeys(_cfg, showSummaryToast: true);

            _hotkeyWnd.HotkeyPressed += (_, e) =>
            {
                if (e.Id == HotkeyMainId)
                {
                    TriggerWithHwnd(e.HwndAtPress, forcedPrompt: null);
                    return;
                }
                int idx = e.Id - HotkeyPromptBaseId; // 0..8
                if (idx >= 0 && idx < 9)
                {
                    var prompts = NormalizePrompts(_cfg);
                    string chosen = prompts[idx];
                    if (string.IsNullOrWhiteSpace(chosen))
                    {
                        _tray.ShowBalloonTip(1800, "ChatMouse", $"Prompt {idx + 1}가 비어 있습니다. Settings에서 설정하세요.", ToolTipIcon.Info);
                        return;
                    }
                    TriggerWithHwnd(e.HwndAtPress, forcedPrompt: chosen);
                }
            };

            ConfigChanged += OnConfigChanged;

            var oneShot = new Timer { Interval = 350 };
            oneShot.Tick += (_, __) =>
            {
                try { oneShot.Stop(); oneShot.Dispose(); } catch { }
                TriggerWithHwnd(GetForegroundWindow(), forcedPrompt: null);
            };
            oneShot.Start();
        }

        private void TriggerWithHwnd(IntPtr hwndAtPress, string? forcedPrompt)
        {
            Logger.Info($"Trigger from tray/hotkey (hwnd=0x{hwndAtPress.ToInt64():X}, forcedPrompt={(forcedPrompt!=null ? "Y" : "N")})");
            _ = TriggerOnceAsync(GetCurrentConfig(), App._httpGlobal!, CancellationToken.None, null, hwndAtPress, forcedPrompt: forcedPrompt);
        }

        protected override void ExitThreadCore()
        {
            Logger.Info("TrayContext Exit");
            try { UnregisterAllHotkeys(); } catch { }
            try { _hotkeyWnd.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { ConfigChanged -= OnConfigChanged; } catch { }
            base.ExitThreadCore();
        }

        private void OnConfigChanged(AppConfig newCfg)
        {
            try
            {
                _uiCtx.Post(_ =>
                {
                    _cfg = newCfg;
                    _http = App._httpGlobal!;

                    if (_miShow != null)
                        _miShow.Text = "Show ( " + _cfg.hotkey + " )";

                    UnregisterAllHotkeys();
                    RegisterAllHotkeys(_cfg, showSummaryToast: true);
                }, null);
            }
            catch (Exception ex)
            {
                Logger.Warn("OnConfigChanged handler error: " + ex.Message);
            }
        }

        private void ShowSettingsDialog()
        {
            try
            {
                using var dlg = new ConfigForm(App.GetCurrentConfig());
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    var newCfg = dlg.GetConfig();
                    App.SaveConfig(newCfg);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ShowSettingsDialog failed");
                MessageBox.Show("설정 창 오류: " + ex.Message, "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RegisterAllHotkeys(AppConfig cfg, bool showSummaryToast)
        {
            _normalizedCombos.Clear();

            int success = 0, fail = 0;
            var failures = new List<string>();

            if (TryRegisterComboUnique(HotkeyMainId, cfg.hotkey, out _, out string errMain))
                success++;
            else { fail++; failures.Add($"Main:{cfg.hotkey} ({errMain})"); }

            var ph = NormalizePromptHotkeys(cfg);
            for (int i = 0; i < 9; i++)
            {
                var hk = ph[i];
                if (string.IsNullOrWhiteSpace(hk)) continue;
                int id = HotkeyPromptBaseId + i;
                if (TryRegisterComboUnique(id, hk, out _, out string err))
                    success++;
                else
                {
                    fail++;
                    failures.Add($"P{i + 1}:{hk} ({err})");
                }
            }

            if (showSummaryToast)
            {
                string summary = fail == 0
                    ? $"Hotkeys ready. Success: {success}"
                    : $"Hotkeys updated. Success: {success}, Fail: {fail}\n{string.Join(", ", failures)}";
                _tray.ShowBalloonTip(1800, "ChatMouse", summary, fail == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
        }

        private bool TryRegisterComboUnique(int id, string hotkey, out string normalized, out string error)
        {
            normalized = "";
            error = "";

            if (!TryNormalizeCombo(hotkey, out string norm, out uint mods, out uint key))
            {
                error = "parse-failed";
                return false;
            }
            normalized = norm;

            if (_normalizedCombos.Contains(norm))
            {
                error = "duplicate-within-config";
                return false;
            }

            _ = UnregisterHotKey(_hotkeyWnd.Handle, id);
            if (!RegisterHotKey(_hotkeyWnd.Handle, id, mods, key))
            {
                int err = Marshal.GetLastWin32Error();
                error = "win32-" + err;
                Logger.Warn($"RegisterHotKey failed ({err}) for '{hotkey}' (mods=0x{mods:X}, key=0x{key:X})");
                return false;
            }

            _registeredIds.Add(id);
            _normalizedCombos.Add(norm);
            return true;
        }

        private void UnregisterAllHotkeys()
        {
            try { UnregisterHotKey(_hotkeyWnd.Handle, HotkeyMainId); } catch { }
            for (int i = 0; i < 9; i++)
            {
                try { UnregisterHotKey(_hotkeyWnd.Handle, HotkeyPromptBaseId + i); } catch { }
            }
            foreach (var id in _registeredIds.ToArray())
            {
                try { UnregisterHotKey(_hotkeyWnd.Handle, id); } catch { }
                _registeredIds.Remove(id);
            }
            _normalizedCombos.Clear();
        }

        // ===== Manual update check entry point =====
        private async Task ManualCheckUpdateAsync(AppConfig cfg, HttpClient http)
        {
            var current = GetCurrentVersionString();
            var latest = await GetLatestReleaseAsync(cfg, http);
            if (latest == null)
            {
                MessageBox.Show("No releases found for the configured repository.", "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool newer = IsNewer(latest.Tag, current);
            if (newer)
            {
                ShowReleaseNotesDialog(latest, isManual: true);
                cfg.last_notified_version = latest.Tag;
                App.SaveConfig(cfg);
            }
            else
            {
                MessageBox.Show($"You're up to date.\nCurrent: {current}\nLatest: {latest.Tag}", "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    private class HotkeyWindow : NativeWindow, IDisposable
    {
        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
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
                IntPtr hwnd = GetForegroundWindow();
                int id = m.WParam.ToInt32();
                HotkeyPressed?.Invoke(this, new HotkeyEventArgs(hwnd, id));
            }
            base.WndProc(ref m);
        }

        public void Dispose() { try { DestroyHandle(); } catch { } }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private static readonly int HotkeyMainId = 0xB00B;
    private static readonly int HotkeyPromptBaseId = 0xB100; // +0..+8 for prompts 1..9

    private static bool TryNormalizeCombo(string s, out string normalized, out uint mods, out uint key)
    {
        normalized = "";
        mods = 0; key = 0;

        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return false;

        var modList = new List<string>();
        string? keyStr = null;

        foreach (var raw in parts)
        {
            var p = raw.ToUpperInvariant();
            switch (p)
            {
                case "CTRL":
                case "CONTROL": if ((mods & MOD_CONTROL) == 0) { mods |= MOD_CONTROL; modList.Add("Ctrl"); } break;
                case "SHIFT": if ((mods & MOD_SHIFT) == 0) { mods |= MOD_SHIFT; modList.Add("Shift"); } break;
                case "ALT": if ((mods & MOD_ALT) == 0) { mods |= MOD_ALT; modList.Add("Alt"); } break;
                case "WIN":
                case "WINDOWS": if ((mods & MOD_WIN) == 0) { mods |= MOD_WIN; modList.Add("Win"); } break;
                default:
                    keyStr ??= p;
                    break;
            }
        }

        if (keyStr == null) keyStr = "SPACE";

        key = NameToVk(keyStr);
        if (key == 0)
        {
            if (keyStr.StartsWith("NUMPAD", StringComparison.OrdinalIgnoreCase) ||
                keyStr.StartsWith("NUM", StringComparison.OrdinalIgnoreCase))
            {
                var d = keyStr.Trim().ToUpperInvariant().Replace("NUMPAD", "").Replace("NUM", "");
                if (int.TryParse(d, out int n) && n >= 0 && n <= 9)
                {
                    key = (uint)(Keys.NumPad0 + n);
                }
            }
        }

        if (key == 0) key = (uint)Keys.Space;

        var keyName = VkToFriendlyName(key);
        normalized = (modList.Count > 0 ? string.Join("+", modList) + "+" : "") + keyName;

        return true;
    }

    private static string VkToFriendlyName(uint vk)
    {
        if (vk >= (uint)Keys.D0 && vk <= (uint)Keys.D9) return ((char)('0' + (vk - (uint)Keys.D0))).ToString();
        if (vk >= (uint)Keys.NumPad0 && vk <= (uint)Keys.NumPad9) return "Num" + (vk - (uint)Keys.NumPad0);
        return Enum.IsDefined(typeof(Keys), (int)vk)
            ? Enum.GetName(typeof(Keys), (int)vk) ?? $"VK_{vk:X2}"
            : $"VK_{vk:X2}";
    }

    private static uint NameToVk(string p)
    {
        if (Enum.TryParse<Keys>(p, true, out var k)) return (uint)k;

        switch (p.ToUpperInvariant())
        {
            case "ESC": case "ESCAPE": return (uint)Keys.Escape;
            case "RET": case "ENTER": return (uint)Keys.Return;
            case "BKSP": case "BACKSPACE": return (uint)Keys.Back;
            case "DEL": case "DELETE": return (uint)Keys.Delete;
            case "TAB": return (uint)Keys.Tab;
            case "SPACE": return (uint)Keys.Space;

            case "0": return (uint)Keys.D0;
            case "1": return (uint)Keys.D1;
            case "2": return (uint)Keys.D2;
            case "3": return (uint)Keys.D3;
            case "4": return (uint)Keys.D4;
            case "5": return (uint)Keys.D5;
            case "6": return (uint)Keys.D6;
            case "7": return (uint)Keys.D7;
            case "8": return (uint)Keys.D8;
            case "9": return (uint)Keys.D9;

            case "NUM0": case "NUMPAD0": return (uint)Keys.NumPad0;
            case "NUM1": case "NUMPAD1": return (uint)Keys.NumPad1;
            case "NUM2": case "NUMPAD2": return (uint)Keys.NumPad2;
            case "NUM3": case "NUMPAD3": return (uint)Keys.NumPad3;
            case "NUM4": case "NUMPAD4": return (uint)Keys.NumPad4;
            case "NUM5": case "NUMPAD5": return (uint)Keys.NumPad5;
            case "NUM6": case "NUMPAD6": return (uint)Keys.NumPad6;
            case "NUM7": case "NUMPAD7": return (uint)Keys.NumPad7;
            case "NUM8": case "NUMPAD8": return (uint)Keys.NumPad8;
            case "NUM9": case "NUMPAD9": return (uint)Keys.NumPad9;

            case "OEM3": return (uint)Keys.Oem3;
            case "OEM7": return (uint)Keys.Oem7;
        }

        return 0;
    }

    #endregion

    #region Trigger Once

    private static async Task TriggerOnceAsync(AppConfig cfg, HttpClient http, CancellationToken externalCt, string? presetContextOrNull, IntPtr hwndAtPress, string? forcedPrompt)
    {
        await Task.Yield();
        Logger.Info($"TriggerOnceAsync begin (hwndAtPress=0x{hwndAtPress.ToInt64():X}, forcedPrompt={(forcedPrompt!=null?"Y":"N")})");

        await WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(250));

        var tooltip = new PrettyTooltipForm("⏳ 선택 텍스트 확인 중…");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        tooltip.FormClosed += (_, __) => { try { cts.Cancel(); cts.Dispose(); } catch { } };

        tooltip.Shown += async (_, __) =>
        {
            try
            {
                tooltip.SetBusy(true); // 요청 시작 전 Busy

                string? context = presetContextOrNull;
                if (string.IsNullOrWhiteSpace(context))
                    context = await GetContextTextPreferSelectionAsync(cfg, cts.Token, hwndAtPress);

                Logger.Info("Context captured: " + (context == null ? "null" : $"len={context.Length}"));
                if (string.IsNullOrWhiteSpace(context)) { tooltip.SetText("📌 선택 텍스트/클립보드 모두 찾지 못했습니다."); return; }

                string[] prompts = NormalizePrompts(cfg);
                string chosen = forcedPrompt ?? prompts[0];
                if (string.IsNullOrWhiteSpace(chosen)) chosen = "다음 텍스트를 요약해줘:";

                tooltip.SetText("⏳ LLM에 요청 중…");

                // per-request timeout
                int seconds = cfg.request_timeout_seconds > 0 ? cfg.request_timeout_seconds : 10;
                using var reqTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, reqTimeout.Token);

                string answer;
                try
                {
                    answer = await QueryLLMAsync(http, cfg, chosen, context!, linked.Token);
                }
                catch (OperationCanceledException) when (reqTimeout.IsCancellationRequested && !cts.IsCancellationRequested)
                {
                    Logger.Warn($"LLM request timeout after {seconds}s");
                    tooltip.SetText($"⏱️ 요청이 제한시간 {seconds}초를 초과했습니다. Settings에서 Request Timeout을 늘려보세요.");
                    return;
                }

                Logger.Info("LLM response len=" + (answer?.Length ?? 0));
                tooltip.SetText(string.IsNullOrEmpty(answer) ? "(빈 응답)" : answer);
            }
            catch (OperationCanceledException)
            { Logger.Info("Trigger canceled (silent)."); tooltip.BeginFadeOut(); return; }
            catch (ObjectDisposedException ode)
            { Logger.Warn("Token disposed during Shown handler: " + ode.Message); tooltip.BeginFadeOut(); return; }
            catch (Exception ex)
            { Logger.Error(ex, "TriggerOnceAsync error"); tooltip.SetText($"❌ Error: {ex.Message}"); }
            finally
            {
                tooltip.StickFor(3500); // 결과 읽을 시간 3.5초
                tooltip.SetBusy(false); // Busy 해제
            }
        };

        tooltip.Show();
        Logger.Info("Tooltip.Show called");
    }

    private static string[] NormalizePrompts(AppConfig cfg)
    {
        var arr = (cfg.prompts ?? Array.Empty<string>()).ToArray();
        if (arr.Length < 9) Array.Resize(ref arr, 9);
        if ((arr.Length == 0 || string.IsNullOrWhiteSpace(arr[0])) && !string.IsNullOrWhiteSpace(cfg.prompt))
        {
            if (arr.Length == 0) Array.Resize(ref arr, 9);
            arr[0] = cfg.prompt;
        }
        if (string.IsNullOrWhiteSpace(arr[0])) arr[0] = "다음 텍스트를 요약해줘:";
        return arr.Select(s => s ?? string.Empty).ToArray();
    }

    private static string[] NormalizePromptHotkeys(AppConfig cfg)
    {
        var arr = (cfg.prompt_hotkeys ?? Array.Empty<string>()).ToArray();
        if (arr.Length < 9) Array.Resize(ref arr, 9);
        for (int i = 0; i < 9; i++)
            if (string.IsNullOrWhiteSpace(arr[i])) arr[i] = "";
        return arr;
    }

    #endregion

    #region HTTP / Config

    private static HttpClient CreateHttp(AppConfig cfg)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(cfg.http_proxy)) { handler.Proxy = new WebProxy(cfg.http_proxy); handler.UseProxy = true; Logger.Info($"Proxy enabled: {cfg.http_proxy}"); }
        if (cfg.disable_ssl_verify) { handler.ServerCertificateCustomValidationCallback = static (_, __, ___, ____) => true; Logger.Warn("SSL verification disabled"); }
        int timeoutSeconds = Math.Max(5, cfg.request_timeout_seconds); // HttpClient.Timeout은 넉넉히(실제 per-request는 개별 토큰으로 제어)
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds + 5) };
    }

    private static AppConfig LoadConfig()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!); } catch { }

        if (!File.Exists(ConfigPath))
        {
            var sample = new AppConfig
            {
                base_url = "http://127.0.0.1:8000/v1",
                api_key = "EMPTY",
                model = "my-vllm-model",
                prompt = "다음 텍스트를 요약해줘:",
                prompts = new[]
                {
                    "다음 텍스트를 요약해줘:",           // 1
                    "다음 텍스트의 핵심 bullet 5개:",    // 2
                    "영어로 번역해줘:",                 // 3
                    "한국어로 요약해줘(한 문단):",        // 4
                    "To-do 항목으로 뽑아줘:",            // 5
                    "", "", "", ""                       // 6~9
                },
                prompt_hotkeys = new[] { "", "", "", "", "", "", "", "", "" },
                allow_clipboard_probe = true,
                http_proxy = null,
                disable_ssl_verify = false,
                tray_mode = true,
                hotkey = "Ctrl+Shift+Space",
                request_timeout_seconds = 12,

                update_check_enabled = true,
                update_repo_owner = "qmoix",
                update_repo_name = "chatmouse",
                last_notified_version = null
            };
            string json = JsonSerializer.Serialize(sample, JsonOpts);
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
            Logger.Info("config.json created with defaults at " + ConfigPath);
            return sample;
        }

        string raw = File.ReadAllText(ConfigPath, Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw, JsonOpts) ?? new AppConfig();

        bool changed = false;

        if (string.IsNullOrWhiteSpace(cfg.base_url)) { cfg.base_url = "http://127.0.0.1:8000/v1"; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.api_key)) { cfg.api_key = "EMPTY"; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.model))   { cfg.model = "my-vllm-model"; changed = true; }

        var prompts = cfg.prompts ?? Array.Empty<string>();
        if (prompts.Length < 9) { Array.Resize(ref prompts, 9); cfg.prompts = prompts; changed = true; }
        if (!string.IsNullOrWhiteSpace(cfg.prompt) && string.IsNullOrWhiteSpace(cfg.prompts[0])) { cfg.prompts[0] = cfg.prompt; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.prompts[0])) { cfg.prompts[0] = "다음 텍스트를 요약해줘:"; changed = true; }

        var ph = cfg.prompt_hotkeys ?? Array.Empty<string>();
        if (ph.Length < 9) { Array.Resize(ref ph, 9); cfg.prompt_hotkeys = ph; changed = true; }

        if (string.IsNullOrWhiteSpace(cfg.hotkey))  { cfg.hotkey = "Ctrl+Shift+Space"; changed = true; }
        if (cfg.request_timeout_seconds <= 0) { cfg.request_timeout_seconds = 12; changed = true; }
        if (!raw.Contains("\"tray_mode\"")) { cfg.tray_mode = true; changed = true; }

        if (cfg.prompt != (cfg.prompts[0] ?? "")) { cfg.prompt = cfg.prompts[0] ?? ""; changed = true; }

        if (!raw.Contains("\"update_check_enabled\"")) { cfg.update_check_enabled = true; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.update_repo_owner)) { cfg.update_repo_owner = "qmoix"; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.update_repo_name)) { cfg.update_repo_name = "chatmouse"; changed = true; }

        if (changed)
        {
            try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts), new UTF8Encoding(false)); }
            catch (Exception ex) { Logger.Warn("Failed to persist defaulted config: " + ex.Message); }
        }

        return cfg;
    }

    private static void StartConfigWatcher()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (dir == null) return;
            string file = Path.GetFileName(ConfigPath);
            Directory.CreateDirectory(dir);
            _cfgWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            FileSystemEventHandler onChange = (_, __) => DebounceReload();
            RenamedEventHandler onRename = (_, __) => DebounceReload();
            _cfgWatcher.Changed += onChange;
            _cfgWatcher.Created += onChange;
            _cfgWatcher.Renamed += onRename;
            _cfgWatcher.Deleted += onChange;
            Logger.Info("Config watcher started at " + dir);
        }
        catch (Exception ex) { Logger.Warn("Config watcher start failed: " + ex.Message); }
    }

    private static void DebounceReload()
    {
        try
        {
            _cfgDebounce?.Dispose();
            _cfgDebounce = new System.Threading.Timer(_ =>
            {
                try { ReloadConfigSafe(); }
                catch (Exception ex) { Logger.Error(ex, "ReloadConfigSafe error"); }
            }, null, 400, System.Threading.Timeout.Infinite);
        }
        catch (Exception ex) { Logger.Warn("Debounce setup failed: " + ex.Message); }
    }

    private static void ReloadConfigSafe()
    {
        var newCfg = LoadConfig();
        Logger.Info($"Using config path: {ConfigPath}");
        _cfgCurrent = newCfg;
        try
        {
            var old = _httpGlobal;
            _httpGlobal = CreateHttp(newCfg);
            try { old?.Dispose(); } catch { }
        }
        catch (Exception ex) { Logger.Warn("HttpClient recreate failed: " + ex.Message); }

        try { ConfigChanged?.Invoke(newCfg); } catch { }
    }

    private static readonly object _cfgSaveLock = new();

    public static void SaveConfig(AppConfig cfg)
    {
        lock (_cfgSaveLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var normP = (cfg.prompts ?? Array.Empty<string>()).ToArray();
                if (normP.Length < 9) Array.Resize(ref normP, 9);
                cfg.prompts = normP;

                var normH = (cfg.prompt_hotkeys ?? Array.Empty<string>()).ToArray();
                if (normH.Length < 9) Array.Resize(ref normH, 9);
                cfg.prompt_hotkeys = normH;

                cfg.prompt = cfg.prompts[0] ?? "";

                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts), new UTF8Encoding(false));
                _cfgCurrent = cfg;

                try
                {
                    var old = _httpGlobal;
                    _httpGlobal = CreateHttp(cfg);
                    try { old?.Dispose(); } catch { }
                }
                catch (Exception ex) { Logger.Warn("HttpClient recreate failed after SaveConfig: " + ex.Message); }

                Logger.Info("SaveConfig: config.json updated by user settings (" + ConfigPath + ").");
                try { ConfigChanged?.Invoke(cfg); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SaveConfig failed");
                MessageBox.Show("설정 저장 실패: " + ex.Message, "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    #endregion

    #region LLM

    private static async Task<string> QueryLLMAsync(HttpClient http, AppConfig cfg, string prompt, string userText, CancellationToken ct)
    {
        string baseUrl = (cfg.base_url ?? "").TrimEnd('/');
        string endpoint = $"{baseUrl}/chat/completions";

        var requestObj = new ChatRequest
        {
            model = cfg.model ?? "",
            messages = new[]
            {
                new ChatMessage { role = "system", content = prompt ?? "" },
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
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Warn("HTTP canceled by token");
            throw;
        }
        catch (Exception ex) { Logger.Error(ex, "HTTP error"); return $"❌ Network error: {ex.Message}"; }
    }

    #endregion

    #region Context (Selection / Clipboard)

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
        IntPtr targetHwnd = hwndAtPress != IntPtr.Zero ? hwndAtPress : GetForegroundWindow();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(900);
            string? uiTxt = await RunOnStaAsync<string?>(() =>
            {
                try
                {
                    using var automation = new UIA3Automation();
                    var element = automation.FocusedElement();
                    if (element == null || targetHwnd != IntPtr.Zero)
                    {
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
                        Thread.Sleep(80);

                        SendCtrlC();
                        Thread.Sleep(140);
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

    #region Update Checker + Release Notes UI

    private static string GetCurrentVersionString()
    {
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly()?.GetName()?.Version;
            if (v == null) return "0.0.0";
            return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build).ToString();
        }
        catch { return "0.0.0"; }
    }

    private static string NormalizeVersionTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "0.0.0";
        tag = tag.Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag[1..];
        var filtered = new string(tag.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "0.0.0" : filtered;
    }

    private static bool IsNewer(string latestTag, string currentVersion)
    {
        string lt = NormalizeVersionTag(latestTag);
        string cur = NormalizeVersionTag(currentVersion);

        if (Version.TryParse(lt, out var vL) && Version.TryParse(cur, out var vC))
            return vL > vC;

        return string.Compare(lt, cur, StringComparison.OrdinalIgnoreCase) > 0;
    }

    public class GhRelease
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("prerelease")] public bool Pre { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    }

    private static async Task<GhRelease?> GetLatestReleaseAsync(AppConfig cfg, HttpClient http)
    {
        if (string.IsNullOrWhiteSpace(cfg.update_repo_owner) || string.IsNullOrWhiteSpace(cfg.update_repo_name))
            return null;

        var url = $"https://api.github.com/repos/{cfg.update_repo_owner.Trim()}/{cfg.update_repo_name.Trim()}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "ChatMouse/1.0 (+https://github.com)");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Logger.Warn($"GitHub latest API failed: {(int)resp.StatusCode} {resp.ReasonPhrase} - {text}");
            return null;
        }

        try
        {
            var rel = JsonSerializer.Deserialize<GhRelease>(text, JsonOpts);
            if (rel == null || rel.Draft) return null;
            return rel;
        }
        catch (Exception ex)
        {
            Logger.Warn("Parse releases JSON failed: " + ex.Message);
            return null;
        }
    }

    private static async Task MaybeNotifyUpdateOnStartupAsync(AppConfig cfg, HttpClient http)
    {
        var current = GetCurrentVersionString();
        var latest = await GetLatestReleaseAsync(cfg, http);
        if (latest == null) return;

        bool newer = IsNewer(latest.Tag, current);
        if (!newer) return;

        if (!string.IsNullOrWhiteSpace(cfg.last_notified_version) &&
            string.Equals(NormalizeVersionTag(cfg.last_notified_version!), NormalizeVersionTag(latest.Tag), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ShowReleaseNotesDialog(latest, isManual: false);
        cfg.last_notified_version = latest.Tag;
        SaveConfig(cfg);
    }

    private static void ShowReleaseNotesDialog(GhRelease rel, bool isManual)
    {
        try
        {
            using var dlg = new ReleaseNotesForm(rel, isManual);
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Warn("ReleaseNotes dialog failed: " + ex.Message);
            MessageBox.Show($"New version available: {rel.Tag}\n\n{Truncate(rel.Body, 800)}",
                "ChatMouse - Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static string Truncate(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n) + " …";
    }

    #endregion
}

#endregion

// ===============================
// Settings UI (ConfigForm)
// ===============================
#region Settings UI

public class ConfigForm : Form
{
    private readonly TextBox tbBaseUrl = new() { Width = 420 };
    private readonly TextBox tbApiKey = new() { Width = 420 };
    private readonly TextBox tbModel = new() { Width = 420 };

    private readonly TextBox[] tbPrompts = Enumerable.Range(0, 9).Select(_ => new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical }).ToArray();
    private readonly TextBox[] tbPromptHotkeys = Enumerable.Range(0, 9).Select(_ => new TextBox()).ToArray();

    private readonly TextBox tbHotkey = new();
    private readonly TextBox tbProxy = new() { Width = 420 };
    private readonly NumericUpDown nudTimeout = new() { Minimum = 5, Maximum = 300, DecimalPlaces = 0 };
    private readonly CheckBox cbAllowClip = new() { Text = "Allow clipboard probe" };
    private readonly CheckBox cbTrayMode = new() { Text = "Start in tray mode" };
    private readonly CheckBox cbSslOff = new() { Text = "Disable SSL verification" };

    // Update checker UI bits
    private readonly CheckBox cbUpdate = new() { Text = "Check updates on startup" };
    private readonly TextBox tbRepoOwner = new() { Width = 180, PlaceholderText = "owner" };
    private readonly TextBox tbRepoName  = new() { Width = 180, PlaceholderText = "repo" };

    private readonly Button btnOk = new() { Text = "OK", DialogResult = DialogResult.OK };
    private readonly Button btnCancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

    private AppConfig _cfg;

    public ConfigForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "ChatMouse Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        Width = 860; Height = 820;

        // 메인 레이아웃
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // 스크롤 영역
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        root.Controls.Add(scroll, 0, 0);

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(4),
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        scroll.Controls.Add(form);

        void AddRow2(string label, Control ctrl)
        {
            int row = form.RowCount++;
            form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 6, 0) }, 0, row);
            form.Controls.Add(ctrl, 1, row);
        }
        void AddRow3(string label, Control left, Control right)
        {
            int row = form.RowCount++;
            form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 6, 0) }, 0, row);
            form.Controls.Add(left, 1, row);
            form.Controls.Add(right, 2, row);
        }

        AddRow2("Base URL:", tbBaseUrl);
        AddRow2("API Key:", tbApiKey);
        AddRow2("Model:", tbModel);

        var prompts = (_cfg.prompts ?? Array.Empty<string>()).ToArray();
        if (prompts.Length < 9) Array.Resize(ref prompts, 9);
        var promptHotkeys = (_cfg.prompt_hotkeys ?? Array.Empty<string>()).ToArray();
        if (promptHotkeys.Length < 9) Array.Resize(ref promptHotkeys, 9);

        // 프롬프트들: 크기를 넉넉히
        for (int i = 0; i < 9; i++)
        {
            var tbP = tbPrompts[i];
            tbP.Width = 520;
            tbP.Height = i == 0 ? 100 : 80;

            var tbH = tbPromptHotkeys[i];
            tbH.Width = 200;

            tbP.Text = prompts[i] ?? (i == 0 ? (_cfg.prompt ?? "") : "");
            tbH.Text = promptHotkeys[i] ?? "";

            AddRow3($"Prompt {i + 1}:", tbP, tbH);
        }

        tbHotkey.Width = 200;
        AddRow2("Main Hotkey:", tbHotkey);

        // 업데이트 체크 UI
        int rowUpdate = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var pnlUpdate = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        pnlUpdate.Controls.Add(cbUpdate);
        pnlUpdate.Controls.Add(new Label { Text = " Owner:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        pnlUpdate.Controls.Add(tbRepoOwner);
        pnlUpdate.Controls.Add(new Label { Text = " Repo:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        pnlUpdate.Controls.Add(tbRepoName);
        form.Controls.Add(new Label { Text = "Updates:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 6, 0) }, 0, rowUpdate);
        form.Controls.Add(pnlUpdate, 1, rowUpdate);

        AddRow2("HTTP Proxy:", tbProxy);

        var pnlTimeout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        nudTimeout.Width = 80;
        pnlTimeout.Controls.Add(nudTimeout);
        pnlTimeout.Controls.Add(new Label { Text = " seconds", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        AddRow2("Request Timeout:", pnlTimeout);

        // 체크 옵션들
        int rowChk = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var pnlChecks = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        pnlChecks.Controls.Add(cbAllowClip);
        pnlChecks.Controls.Add(cbTrayMode);
        pnlChecks.Controls.Add(cbSslOff);
        form.Controls.Add(new Label { Text = "", AutoSize = true }, 0, rowChk);
        form.Controls.Add(pnlChecks, 1, rowChk);

        // 버튼 바
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 8) };
        btnOk.AutoSize = true; btnCancel.AutoSize = true;
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnCancel);
        root.Controls.Add(btnPanel, 0, 1);

        // 값 채우기
        tbBaseUrl.Text = _cfg.base_url;
        tbApiKey.Text = _cfg.api_key;
        tbModel.Text = _cfg.model;

        tbHotkey.Text = _cfg.hotkey;
        tbProxy.Text = _cfg.http_proxy ?? "";
        nudTimeout.Value = _cfg.request_timeout_seconds > 0 ? _cfg.request_timeout_seconds : 12;
        cbAllowClip.Checked = _cfg.allow_clipboard_probe;
        cbTrayMode.Checked = _cfg.tray_mode;
        cbSslOff.Checked = _cfg.disable_ssl_verify;

        cbUpdate.Checked = _cfg.update_check_enabled;
        tbRepoOwner.Text = _cfg.update_repo_owner ?? "";
        tbRepoName.Text  = _cfg.update_repo_name  ?? "";

        btnOk.Click += (_, __) => { DialogResult = DialogResult.OK; Close(); };
        btnCancel.Click += (_, __) => { DialogResult = DialogResult.Cancel; Close(); };
    }

    public AppConfig GetConfig()
    {
        var prompts = tbPrompts.Select(tb => (tb.Text ?? "").Trim()).ToArray();
        if (prompts.Length < 9) Array.Resize(ref prompts, 9);

        var hotkeys = tbPromptHotkeys.Select(tb => (tb.Text ?? "").Trim()).ToArray();
        if (hotkeys.Length < 9) Array.Resize(ref hotkeys, 9);

        return new AppConfig
        {
            base_url = tbBaseUrl.Text.Trim(),
            api_key = tbApiKey.Text.Trim(),
            model = tbModel.Text.Trim(),

            prompt = prompts[0] ?? "",
            prompts = prompts,
            prompt_hotkeys = hotkeys,

            hotkey = tbHotkey.Text.Trim(),
            allow_clipboard_probe = cbAllowClip.Checked,
            tray_mode = cbTrayMode.Checked,
            http_proxy = string.IsNullOrWhiteSpace(tbProxy.Text) ? null : tbProxy.Text.Trim(),
            disable_ssl_verify = cbSslOff.Checked,
            request_timeout_seconds = (int)nudTimeout.Value,

            update_check_enabled = cbUpdate.Checked,
            update_repo_owner = tbRepoOwner.Text.Trim(),
            update_repo_name = tbRepoName.Text.Trim(),

            last_notified_version = App.GetCurrentConfig().last_notified_version
        };
    }
}

#endregion

#region Release Notes Form

public class ReleaseNotesForm : Form
{
    public ReleaseNotesForm(App.GhRelease rel, bool isManual)
    {
        Text = $"ChatMouse Update — {rel.Tag}";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 720; Height = 600;
        MinimumSize = new Size(560, 420);

        var lblTitle = new Label
        {
            Text = string.IsNullOrWhiteSpace(rel.Name) ? rel.Tag : rel.Name,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            AutoSize = false,
            Height = 48
        };

        var tb = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10f, FontStyle.Regular),
            Text = string.IsNullOrWhiteSpace(rel.Body) ? "(no release notes)" : rel.Body
        };

        var info = new Label
        {
            Text = $"Latest: {rel.Tag}    Published: {(rel.PublishedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-")}",
            Dock = DockStyle.Top,
            Padding = new Padding(12, 0, 12, 8),
            AutoSize = false,
            Height = 28
        };

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 56
        };

        var btnClose = new Button { Text = isManual ? "Close" : "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var btnCopy  = new Button { Text = "Copy notes", AutoSize = true };
        btnCopy.Click += (_, __) =>
        {
            try { Clipboard.SetText(tb.Text); } catch { }
        };

        panelButtons.Controls.Add(btnClose);
        panelButtons.Controls.Add(btnCopy);

        Controls.Add(tb);
        Controls.Add(panelButtons);
        Controls.Add(info);
        Controls.Add(lblTitle);
    }
}

#endregion
