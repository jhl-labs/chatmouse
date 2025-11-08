// Program.cs - ChatMouse
// - Single-instance (WM_COPYDATA IPC)
// - Tooltip with fade, selectable Text mode, close button
// - CRLF normalization for consistent wrapping
// - Config in %USERPROFILE%\.chatmouse\config.json
// - Multiple prompts (1..9) with INDIVIDUAL GLOBAL HOTKEYS per prompt
// - Settings: Tabbed UI (General / Prompts & Hotkeys / LLM / Network / Updates)
// - Hotkey: robust parsing, unregister/re-register all on settings change, one toast summary
// - Tooltip lifetime configurable; error auto-hide but NOT during active LLM request
// - LLM call: detailed logging of request when error (URL, token first 10 chars, model, headers, prompts)
// - Update checker (GitHub) with env override & .env support

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

public class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}
public class ChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("messages")] public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.7;
}
public class ChatChoice
{
    [JsonPropertyName("message")] public ChatMessage Message { get; set; } = new ChatMessage();
}
public class ChatResponse
{
    [JsonPropertyName("choices")] public ChatChoice[] Choices { get; set; } = Array.Empty<ChatChoice>();
}

#endregion

#region UI Constants

static class Ui
{
    public const int Corner = 12;
    public static readonly Padding Pad = new Padding(16, 12, 16, 14);
    public const int MaxWidth = 900;
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
    private readonly Timer _errorAutoClose = new() { Interval = 1000 };
    private int _errorRemainMs = 0;

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
    private int _graceMs = 3000;
    private bool _isMouseOver = false;
    private bool _useTextBox = false;

    private DateTime _approachUntil;
    private bool _hasApproached = false;
    private DateTime? _awaySince = null;
    private const int MaxIdleMsWithoutApproach = 8000;

    private bool _llmInFlight = false;

        // DPI-aware layout fields
        private Padding _pad;
        private int _corner;
        private float _dpiScale = 1f;

    public PrettyTooltipForm(string initialText, int stayMs, Point? anchorOverride = null)
    {
        _textRaw = initialText ?? "";
        _text = NormalizeNewlines(_textRaw);

        _graceMs = Math.Max(1500, stayMs);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 24);
        Opacity = 0.0;

                // DPI-aware scaling for this tooltip window
                AutoScaleMode = AutoScaleMode.Dpi;
                try { _dpiScale = DeviceDpi / 96f; } catch { _dpiScale = 1f; }
                _corner = (int)Math.Round(Ui.Corner * _dpiScale);
                _pad = new Padding(
                    (int)Math.Round(Ui.Pad.Left * _dpiScale),
                    (int)Math.Round(Ui.Pad.Top * _dpiScale),
                    (int)Math.Round(Ui.Pad.Right * _dpiScale),
                    (int)Math.Round(Ui.Pad.Bottom * _dpiScale)
                );

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
        MouseLeave += (s, e) => { _isMouseOver = false; if (_life.ElapsedMilliseconds >= _graceMs && !_useTextBox && !_llmInFlight) _cursorWatch.Start(); };
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
            if (DateTime.Now < _approachUntil) return;
            if (_llmInFlight) return; // 요청 중엔 사라지지 않음

            if (!_hasApproached)
            {
                if (_life.ElapsedMilliseconds >= Math.Max(MaxIdleMsWithoutApproach, _graceMs))
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

        _errorAutoClose.Tick += (_, __) =>
        {
            if (_errorRemainMs <= 0)
            {
                _errorAutoClose.Stop();
                if (!_llmInFlight) BeginFadeOut();
                return;
            }
            _errorRemainMs -= _errorAutoClose.Interval;
        };

        _anchorCursor = anchorOverride ?? Cursor.Position;
        RecalcSizeAndPlaceNearCursor(_anchorCursor);
    }

    public void MarkLlmInFlight(bool inFlight)
    {
        _llmInFlight = inFlight;
        if (inFlight)
        {
            _cursorWatch.Stop();
        }
        else
        {
            _approachUntil = DateTime.Now.AddMilliseconds(1200);
            _cursorWatch.Start();
        }
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

    public void ShowErrorThenAutoClose(string errorText, int autoCloseMs)
    {
        SetText(errorText);
        _llmInFlight = false;
        _errorRemainMs = Math.Max(1500, autoCloseMs);
        _errorAutoClose.Stop();
        _errorAutoClose.Start();
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
        _textBox.Size = new Size(Width - _pad.Horizontal, Height - _pad.Vertical);
        _textBox.Location = new Point(_pad.Left, _pad.Top);
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
        _mode = AnimMode.FadeOut;
        _animStart = DateTime.Now;
        _animTimer.Start();
        Logger.Info("Tooltip fade-out started");
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
                size = new Size(Math.Max(240, measured.Width + Ui.Pad.Horizontal + 6),
                                Math.Max(120, measured.Height + Ui.Pad.Vertical + 6));
            }
        }
        else
        {
            Size proposed = new(Ui.MaxWidth, int.MaxValue);
            Size measured = TextRenderer.MeasureText(string.IsNullOrEmpty(_text) ? " " : _text, Ui.Font, proposed,
                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            size = new Size(Math.Max(240, measured.Width + Ui.Pad.Horizontal + 6),
                            Math.Max(120, measured.Height + Ui.Pad.Vertical + 6));
        }

        Size = size;

        using (var gp = new GraphicsPath())
        { gp.AddRectangle(new Rectangle(0, 0, size.Width, size.Height)); Region = new Region(gp); }

        Rectangle workArea = Screen.FromPoint(anchor).WorkingArea;
        int x = anchor.X + SpawnOffset;
        int y = anchor.Y + SpawnOffset;

        // Adjust X position if tooltip goes off-screen horizontally
        if (x + size.Width > workArea.Right) x = workArea.Right - size.Width - SpawnOffset;
        if (x < workArea.Left) x = workArea.Left + SpawnOffset;

        // Adjust Y position if tooltip goes off-screen vertically
        // Instead of moving to bottom, show above the anchor point
        if (y + size.Height > workArea.Bottom)
        {
            // Try to show above the anchor
            y = anchor.Y - size.Height - SpawnOffset;
            // If still doesn't fit, clamp to workArea
            if (y < workArea.Top) y = workArea.Top + SpawnOffset;
        }
        if (y < workArea.Top) y = workArea.Top + SpawnOffset;

        var final = new Point(x, y);
        Location = final;

        LayoutInner();

        try
        {
            Logger.Info($"Tooltip placed: anchor=({anchor.X},{anchor.Y}), workArea=({workArea.Left},{workArea.Top},{workArea.Right},{workArea.Bottom}), size=({size.Width}x{size.Height}), final=({final.X},{final.Y})");
        }
        catch { }
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
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("api_key")] public string ApiKey { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty; // legacy 1st prompt
    [JsonPropertyName("prompts")] public string[] Prompts { get; set; } = Array.Empty<string>();
    [JsonPropertyName("prompt_hotkeys")] public string[] PromptHotkeys { get; set; } = Array.Empty<string>();

    [JsonPropertyName("allow_clipboard_probe")] public bool AllowClipboardProbe { get; set; } = true;
    [JsonPropertyName("http_proxy")] public string? HttpProxy { get; set; }
    [JsonPropertyName("disable_ssl_verify")] public bool DisableSslVerify { get; set; } = false;

    [JsonPropertyName("tray_mode")] public bool TrayMode { get; set; } = true;
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "Ctrl+Shift+Space";
    [JsonPropertyName("request_timeout_seconds")] public int RequestTimeoutSeconds { get; set; } = 10;

    [JsonPropertyName("tooltip_stay_ms")] public int TooltipStayMs { get; set; } = 6000;  // 일반 유지시간
    [JsonPropertyName("tooltip_error_ms")] public int TooltipErrorMs { get; set; } = 3500; // 에러 표시 후 자동 닫힘

    // LLM custom headers: "Header: Value" per line in UI
    [JsonPropertyName("llm_custom_headers")] public string[] LlmCustomHeaders { get; set; } = Array.Empty<string>();

    // ===== Update checker config =====
    [JsonPropertyName("update_check_enabled")] public bool UpdateCheckEnabled { get; set; } = true;
    [JsonPropertyName("update_repo_owner")] public string UpdateRepoOwner { get; set; } = "qmoix";
    [JsonPropertyName("update_repo_name")] public string UpdateRepoName { get; set; } = "chatmouse";
    [JsonPropertyName("last_notified_version")] public string? LastNotifiedVersion { get; set; }
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
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); Logger.Info("DPI awareness: PerMonitorV2 set"); } catch { Logger.Warn("Failed to set PerMonitorV2 DPI awareness (continuing)"); }
        Mutex? mutex = null;
        try
        {
            // Load .env if exists (to populate Environment for this process)
            TryLoadDotEnv();

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

            var cfg = LoadConfigWithEnvOverrides();
            _cfgCurrent = cfg;
            Logger.Info($"Config loaded. tray_mode={cfg.TrayMode}, hotkey={cfg.Hotkey}, base_url={cfg.BaseUrl}, model={cfg.Model}, proxy={(cfg.HttpProxy ?? "null")}, ssl_off={cfg.DisableSslVerify}, tooltip_stay_ms={cfg.TooltipStayMs}, tooltip_error_ms={cfg.TooltipErrorMs}");

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

            // Update check on startup
            if (cfg.UpdateCheckEnabled)
            {
                _ = Task.Run(async () =>
                {
                    try { await MaybeNotifyUpdateOnStartupAsync(cfg, _httpGlobal!); }
                    catch (Exception ex) { Logger.Warn("Startup update check failed: " + ex.Message); }
                });
            }

            if (cfg.TrayMode)
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

    private static void TryLoadDotEnv()
    {
        try
        {
            string dir = GetConfigDirectory();
            string envPath1 = Path.Combine(dir, ".env");
            string envPath2 = Path.Combine(AppContext.BaseDirectory, ".env");
            string path = File.Exists(envPath1) ? envPath1 : (File.Exists(envPath2) ? envPath2 : "");
            if (string.IsNullOrEmpty(path)) return;

            foreach (var line in File.ReadAllLines(path))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                string key = s.Substring(0, eq).Trim();
                string val = s.Substring(eq + 1).Trim().Trim('"');
                Environment.SetEnvironmentVariable(key, val, EnvironmentVariableTarget.Process);
            }
            Logger.Info(".env loaded");
        }
        catch (Exception ex) { Logger.Warn(".env load failed: " + ex.Message); }
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
            _miShow = new ToolStripMenuItem("Show ( " + (_cfg.Hotkey) + " )");
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

            // Removed auto-trigger on tray startup: do not show LLM tooltip immediately when starting in tray mode.
            // Original behavior used a one-shot timer to call TriggerWithHwnd shortly after startup.
            // This is intentionally disabled to honor the requirement that tray mode should be silent on launch.
        }

        private void TriggerWithHwnd(IntPtr hwndAtPress, string? forcedPrompt)
        {
            Logger.Info($"Trigger from tray/hotkey (hwnd=0x{hwndAtPress.ToInt64():X}, forcedPrompt={(forcedPrompt!=null ? "Y" : "N")})");
            var http = App._httpGlobal ?? App.CreateHttp(_cfg);
            _http = http;
            _ = TriggerOnceAsync(GetCurrentConfig(), http, CancellationToken.None, null, hwndAtPress, forcedPrompt: forcedPrompt);
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
                    // Decide whether hotkey re-registration is actually needed
                    string oldMain = _cfg.Hotkey ?? string.Empty;
                    string newMain = newCfg.Hotkey ?? string.Empty;

                    bool TryNorm(string s, out string norm)
                    {
                        norm = string.Empty;
                        if (string.IsNullOrWhiteSpace(s)) return false;
                        string n;
                        uint m, k;
                        if (TryNormalizeCombo(s, out n, out m, out k)) { norm = n; return true; }
                        return false;
                    }

                    bool mainChanged = true;
                    if (TryNorm(oldMain, out var normOldMain) && TryNorm(newMain, out var normNewMain))
                        mainChanged = !string.Equals(normOldMain, normNewMain, StringComparison.OrdinalIgnoreCase);
                    else
                        mainChanged = !string.Equals(oldMain?.Trim(), newMain?.Trim(), StringComparison.OrdinalIgnoreCase);

                    string[] oldPh = NormalizePromptHotkeys(_cfg);
                    string[] newPh = NormalizePromptHotkeys(newCfg);
                    bool promptsChanged = false;
                    for (int i = 0; i < 9; i++)
                    {
                        string a = oldPh[i] ?? string.Empty;
                        string b = newPh[i] ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) continue;
                        bool hasA = TryNorm(a, out var na);
                        bool hasB = TryNorm(b, out var nb);
                        if (hasA && hasB)
                        {
                            if (!string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) { promptsChanged = true; break; }
                        }
                        else if (!string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) { promptsChanged = true; break; }
                    }

                    bool hotkeysChanged = mainChanged || promptsChanged;

                    // Adopt new config and HttpClient regardless
                    _cfg = newCfg;
                    _http = App._httpGlobal ?? App.CreateHttp(newCfg);

                    if (_miShow != null)
                        _miShow.Text = "Show ( " + _cfg.Hotkey + " )";

                    if (hotkeysChanged)
                    {
                        UnregisterAllHotkeys();
                        RegisterAllHotkeys(_cfg, showSummaryToast: true);
                    }
                    else
                    {
                        // Do not re-register when hotkeys are unchanged (prevents spurious 1408 errors and noisy toast)
                        Logger.Info("ConfigChanged: LLM/settings updated without hotkey changes — keeping existing hotkey registrations.");
                    }
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

        // ===== Strong UI-thread marshalling for hotkeys =====
        private void RegisterAllHotkeys(AppConfig cfg, bool showSummaryToast)
        {
            if (SynchronizationContext.Current == _uiCtx)
                RegisterAllHotkeys_UI(cfg, showSummaryToast);
            else
                _uiCtx.Send(_ => RegisterAllHotkeys_UI(cfg, showSummaryToast), null);
        }
        private void UnregisterAllHotkeys()
        {
            if (SynchronizationContext.Current == _uiCtx)
                UnregisterAllHotkeys_UI();
            else
                _uiCtx.Send(_ => UnregisterAllHotkeys_UI(), null);
        }

        private void RegisterAllHotkeys_UI(AppConfig cfg, bool showSummaryToast)
        {
            _normalizedCombos.Clear();

            int success = 0, fail = 0;
            var failures = new List<string>();

            if (TryRegisterComboUnique(HotkeyMainId, cfg.Hotkey, out _, out string errMain))
                success++;
            else { fail++; failures.Add($"Main:{cfg.Hotkey} ({errMain})"); }

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

        private void UnregisterAllHotkeys_UI()
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

            // ensure unregister on UI thread
            _uiCtx.Send(_ => { try { UnregisterHotKey(_hotkeyWnd.Handle, id); } catch { } }, null);

            bool ok = false;
            int winErr = 0;

            // first try (we're on UI thread here)
            if (!RegisterHotKey(_hotkeyWnd.Handle, id, mods, key))
            {
                winErr = Marshal.GetLastWin32Error();

                if (winErr == 1408) // ERROR_WINDOW_OF_OTHER_THREAD
                {
                    // re-try on UI thread explicitly
                    _uiCtx.Send(_ =>
                    {
                        if (!RegisterHotKey(_hotkeyWnd.Handle, id, mods, key))
                        {
                            winErr = Marshal.GetLastWin32Error();
                            ok = false;
                        }
                        else ok = true;
                    }, null);
                }
                else if (winErr == 1409) // already registered by other app / reserved
                {
                    error = "already-registered-or-reserved";
                    return false;
                }
                else
                {
                    error = "win32-" + winErr;
                    return false;
                }
            }
            else ok = true;

            if (!ok)
            {
                error = winErr == 1408 ? "other-thread-hwnd" : "win32-" + winErr;
                Logger.Warn($"RegisterHotKey failed ({winErr}) for '{hotkey}'");
                return false;
            }

            _registeredIds.Add(id);
            _normalizedCombos.Add(norm);
            return true;
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
                cfg.LastNotifiedVersion = latest.Tag;
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

        Point anchor = TryGetCaretOrCursorAnchor(hwndAtPress);
                var tooltip = new PrettyTooltipForm("⏳ 선택 텍스트 확인 중…", cfg.TooltipStayMs, anchor);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        tooltip.FormClosed += (_, __) => { try { cts.Cancel(); cts.Dispose(); } catch { } };

        tooltip.Shown += async (_, __) =>
        {
            try
            {
                // Prevent auto-close while we're busy capturing context and calling LLM
                tooltip.MarkLlmInFlight(true);

                string? context = presetContextOrNull;
                if (string.IsNullOrWhiteSpace(context))
                    context = await GetContextTextPreferSelectionAsync(cfg, cts.Token, hwndAtPress);

                Logger.Info("Context captured: " + (context == null ? "null" : $"len={context.Length}"));
                if (string.IsNullOrWhiteSpace(context)) { tooltip.MarkLlmInFlight(false); tooltip.SetText("📌 선택 텍스트/클립보드 모두 찾지 못했습니다."); return; }

                string[] prompts = NormalizePrompts(cfg);
                string chosen = forcedPrompt ?? prompts[0];
                if (string.IsNullOrWhiteSpace(chosen)) chosen = "다음 텍스트를 요약해줘:";

                tooltip.SetText("⏳ LLM에 요청 중…");

                string answer = await QueryLLMAsync(http, cfg, chosen, context!, cts.Token);
                Logger.Info("LLM response len=" + (answer?.Length ?? 0));

                if (tooltip.IsHandleCreated && !tooltip.IsDisposed)
                {
                    tooltip.BeginInvoke(new Action(() =>
                    {
                        tooltip.MarkLlmInFlight(false);
                        tooltip.SetText(string.IsNullOrEmpty(answer) ? "(빈 응답)" : answer);
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Trigger canceled (silent).");
                if (tooltip.IsHandleCreated && !tooltip.IsDisposed)
                {
                    tooltip.BeginInvoke(new Action(() =>
                    {
                        tooltip.MarkLlmInFlight(false);
                        tooltip.ShowErrorThenAutoClose("⏹️ 취소됨", cfg.TooltipErrorMs);
                    }));
                }
                return;
            }
            catch (ObjectDisposedException ode)
            {
                Logger.Warn("Token disposed during Shown handler: " + ode.Message);
                if (tooltip.IsHandleCreated && !tooltip.IsDisposed)
                {
                    tooltip.BeginInvoke(new Action(() =>
                    {
                        tooltip.MarkLlmInFlight(false);
                        tooltip.ShowErrorThenAutoClose("⚠️ Disposed", cfg.TooltipErrorMs);
                    }));
                }
                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TriggerOnceAsync error");
                if (tooltip.IsHandleCreated && !tooltip.IsDisposed)
                {
                    tooltip.BeginInvoke(new Action(() =>
                    {
                        tooltip.MarkLlmInFlight(false);
                        tooltip.ShowErrorThenAutoClose($"❌ Error: {ex.Message}", cfg.TooltipErrorMs);
                    }));
                }
            }
        };

        tooltip.Show();
        Logger.Info("Tooltip.Show called");
    }

    private static string[] NormalizePrompts(AppConfig cfg)
    {
        var arr = (cfg.Prompts ?? Array.Empty<string>()).ToArray();
        if (arr.Length < 9) Array.Resize(ref arr, 9);
        if ((arr.Length == 0 || string.IsNullOrWhiteSpace(arr[0])) && !string.IsNullOrWhiteSpace(cfg.Prompt))
        {
            if (arr.Length == 0) Array.Resize(ref arr, 9);
            arr[0] = cfg.Prompt;
        }
        if (string.IsNullOrWhiteSpace(arr[0])) arr[0] = "다음 텍스트를 요약해줘:";
        return arr.Select(s => s ?? string.Empty).ToArray();
    }

    private static string[] NormalizePromptHotkeys(AppConfig cfg)
    {
        var arr = (cfg.PromptHotkeys ?? Array.Empty<string>()).ToArray();
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
        if (!string.IsNullOrWhiteSpace(cfg.HttpProxy)) { handler.Proxy = new WebProxy(cfg.HttpProxy); handler.UseProxy = true; Logger.Info($"Proxy enabled: {cfg.HttpProxy}"); }
        if (cfg.DisableSslVerify) { handler.ServerCertificateCustomValidationCallback = static (_, __, ___, ____) => true; Logger.Warn("SSL verification disabled"); }
        int timeoutSeconds = cfg.RequestTimeoutSeconds > 0 ? cfg.RequestTimeoutSeconds : 10;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    private static AppConfig LoadConfigWithEnvOverrides()
    {
        var cfg = LoadConfig();

        // Environment overrides for update repo owner/name
        string? envOwner = "jong-hun-lee";
        string? envRepo = "chatmouse";
        if (!string.IsNullOrWhiteSpace(envOwner)) cfg.UpdateRepoOwner = envOwner.Trim();
        if (!string.IsNullOrWhiteSpace(envRepo))  cfg.UpdateRepoName  = envRepo.Trim();

        return cfg;
    }

    private static AppConfig LoadConfig()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!); } catch { }

        if (!File.Exists(ConfigPath))
        {
            var sample = new AppConfig
            {
                BaseUrl = "http://127.0.0.1:8000/v1",
                ApiKey = "EMPTY",
                Model = "my-vllm-model",
                Prompt = "다음 텍스트를 요약해줘:",
                Prompts = new[]
                {
                    "다음 텍스트를 요약해줘:",           // 1
                    "다음 텍스트의 핵심 bullet 5개:",    // 2
                    "영어로 번역해줘:",                 // 3
                    "한국어로 요약해줘(한 문단):",        // 4
                    "To-do 항목으로 뽑아줘:",            // 5
                    "", "", "", ""                       // 6~9
                },
                PromptHotkeys = new[] { "", "", "", "", "", "", "", "", "" },
                AllowClipboardProbe = true,
                HttpProxy = null,
                DisableSslVerify = false,
                TrayMode = true,
                Hotkey = "Ctrl+Shift+Space",
                RequestTimeoutSeconds = 10,

                TooltipStayMs = 6000,
                TooltipErrorMs = 3500,
                LlmCustomHeaders = Array.Empty<string>(),

                UpdateCheckEnabled = true,
                UpdateRepoOwner = "qmoix",
                UpdateRepoName = "chatmouse",
                LastNotifiedVersion = null
            };
            string json = JsonSerializer.Serialize(sample, JsonOpts);
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
            Logger.Info("config.json created with defaults at " + ConfigPath);
            return sample;
        }

        string raw = File.ReadAllText(ConfigPath, Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize<AppConfig>(raw, JsonOpts) ?? new AppConfig();

        bool changed = false;

        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) { cfg.BaseUrl = "http://127.0.0.1:8000/v1"; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) { cfg.ApiKey = "EMPTY"; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.Model))   { cfg.Model = "my-vllm-model"; changed = true; }

        var prompts = cfg.Prompts ?? Array.Empty<string>();
        if (prompts.Length < 9) { Array.Resize(ref prompts, 9); cfg.Prompts = prompts; changed = true; }
        if (!string.IsNullOrWhiteSpace(cfg.Prompt) && string.IsNullOrWhiteSpace(cfg.Prompts[0])) { cfg.Prompts[0] = cfg.Prompt; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.Prompts[0])) { cfg.Prompts[0] = "다음 텍스트를 요약해줘:"; changed = true; }

        var ph = cfg.PromptHotkeys ?? Array.Empty<string>();
        if (ph.Length < 9) { Array.Resize(ref ph, 9); cfg.PromptHotkeys = ph; changed = true; }

        if (string.IsNullOrWhiteSpace(cfg.Hotkey))  { cfg.Hotkey = "Ctrl+Shift+Space"; changed = true; }
        if (cfg.RequestTimeoutSeconds <= 0) { cfg.RequestTimeoutSeconds = 10; changed = true; }
        if (!raw.Contains("\"tray_mode\"")) { cfg.TrayMode = true; changed = true; }

        if (cfg.Prompt != (cfg.Prompts[0] ?? "")) { cfg.Prompt = cfg.Prompts[0] ?? ""; changed = true; }

        if (!raw.Contains("\"tooltip_stay_ms\"")) { cfg.TooltipStayMs = 6000; changed = true; }
        if (!raw.Contains("\"tooltip_error_ms\"")) { cfg.TooltipErrorMs = 3500; changed = true; }
        if (cfg.LlmCustomHeaders == null) { cfg.LlmCustomHeaders = Array.Empty<string>(); changed = true; }

        if (!raw.Contains("\"update_check_enabled\"")) { cfg.UpdateCheckEnabled = true; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.UpdateRepoOwner)) { cfg.UpdateRepoOwner = "qmoix"; changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.UpdateRepoName)) { cfg.UpdateRepoName = "chatmouse"; changed = true; }

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
        var newCfg = LoadConfigWithEnvOverrides();
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

                var normP = (cfg.Prompts ?? Array.Empty<string>()).ToArray();
                if (normP.Length < 9) Array.Resize(ref normP, 9);
                cfg.Prompts = normP;

                var normH = (cfg.PromptHotkeys ?? Array.Empty<string>()).ToArray();
                if (normH.Length < 9) Array.Resize(ref normH, 9);
                cfg.PromptHotkeys = normH;

                cfg.Prompt = cfg.Prompts[0] ?? "";
                if (cfg.TooltipStayMs < 1500) cfg.TooltipStayMs = 1500;
                if (cfg.TooltipErrorMs < 1500) cfg.TooltipErrorMs = 1500;

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

    private static Dictionary<string, string> ParseCustomHeaders(string[]? lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines == null || lines.Length == 0) return dict;

        // JSON 형식인지 확인 (첫 번째 줄이 {로 시작하고 마지막 줄이 }로 끝나는 경우)
        string combined = string.Join("", lines.Select(l => (l ?? "").Trim()));
        if (combined.StartsWith("{") && combined.EndsWith("}"))
        {
            try
            {
                var jsonDict = JsonSerializer.Deserialize<Dictionary<string, string>>(combined, JsonOpts);
                if (jsonDict != null)
                {
                    foreach (var kv in jsonDict)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key))
                            dict[kv.Key] = kv.Value ?? "";
                    }
                }
                return dict;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to parse custom headers as JSON: {ex.Message}");
                // JSON 파싱 실패 시 기존 방식으로 fallback
            }
        }

        // 기존 "Header: Value" 형식 파싱
        foreach (var raw in lines)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) continue;
            int p = s.IndexOf(':');
            if (p <= 0) continue;
            var key = s.Substring(0, p).Trim();
            var val = s.Substring(p + 1).Trim();
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    private static async Task<string> QueryLLMAsync(HttpClient http, AppConfig cfg, string prompt, string userText, CancellationToken ct)
    {
        string baseUrl = (cfg.BaseUrl ?? "").TrimEnd('/');
        string endpoint = $"{baseUrl}/chat/completions";

        var requestObj = new ChatRequest
        {
            Model = cfg.Model ?? "",
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = prompt ?? "" },
                new ChatMessage { Role = "user", Content = userText }
            }
        };

        string json = JsonSerializer.Serialize(requestObj, JsonOpts);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        // Auth & custom headers
        bool hasBearer = !string.Equals(cfg.ApiKey, "EMPTY", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cfg.ApiKey);
        if (hasBearer) httpReq.Headers.Add("Authorization", $"Bearer {cfg.ApiKey}");
        var extra = ParseCustomHeaders(cfg.LlmCustomHeaders);
        foreach (var kv in extra) { try { httpReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { } }

        try
        {
            Logger.Info("HTTP POST " + endpoint);
            var resp = await http.SendAsync(httpReq, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Logger.Info($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}, {body.Length} bytes");

            if (!resp.IsSuccessStatusCode)
            {
                LogRequestDetailsForError(endpoint, cfg, prompt, userText, extra, resp.StatusCode + " " + resp.ReasonPhrase);
                return $"❌ Request failed ({(int)resp.StatusCode}): {resp.ReasonPhrase}";
            }

            try
            {
                var chatResp = JsonSerializer.Deserialize<ChatResponse>(body, JsonOpts);
                string? content = chatResp?.Choices?.Length > 0 ? chatResp!.Choices[0].Message?.Content : null;
                return string.IsNullOrWhiteSpace(content) ? "(no response)" : content!.Trim();
            }
            catch (Exception ex) { Logger.Error(ex, "Parse LLM JSON failed"); LogRequestDetailsForError(endpoint, cfg, prompt, userText, extra, "parse-json"); return "⚠️ Failed to parse JSON response"; }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { Logger.Info("HTTP canceled"); throw; }
        catch (Exception ex)
        {
            Logger.Error(ex, "HTTP error");
            LogRequestDetailsForError(endpoint, cfg, prompt, userText, extra, "exception:" + ex.GetType().Name);
            return $"❌ Network error: {ex.Message}";
        }
    }

    private static void LogRequestDetailsForError(string url, AppConfig cfg, string sysPrompt, string userPrompt, Dictionary<string, string> extraHeaders, string note)
    {
        string token10 = (cfg.ApiKey ?? "").Length > 10 ? cfg.ApiKey.Substring(0, 10) : cfg.ApiKey ?? "";
        var headerDump = string.Join(", ", extraHeaders.Select(kv => kv.Key + "=" + kv.Value));
        Logger.Warn($"LLM-REQ-ERROR [{note}] url={url} model={cfg.Model} token10='{token10}' headers=[{headerDump}] sys='{Trunc(sysPrompt, 120)}' user='{Trunc(userPrompt, 120)}'");
    }

    private static string Trunc(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n) + " …";
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
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETSEL = 0x00B0;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
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

    private static Point TryGetCaretOrCursorAnchor(IntPtr hwndAtPress)
    {
        try
        {
            IntPtr targetHwnd = hwndAtPress != IntPtr.Zero ? hwndAtPress : GetForegroundWindow();
            if (targetHwnd != IntPtr.Zero)
            {
                uint tid = GetWindowThreadProcessId(targetHwnd, out _);
                GUITHREADINFO gti = new() { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(tid, ref gti))
                {
                    if (gti.hwndCaret != IntPtr.Zero)
                    {
                        POINT pt = new POINT { x = gti.rcCaret.left, y = gti.rcCaret.top };
                        if (ClientToScreen(gti.hwndCaret, ref pt))
                        {
                            var p = new Point(pt.x, pt.y);
                            Logger.Info($"Anchor chosen: caret ({p.X},{p.Y})");
                            return p;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("TryGetCaretOrCursorAnchor failed: " + ex.Message);
        }
        var cur = Cursor.Position;
        Logger.Info($"Anchor chosen: cursor ({cur.X},{cur.Y})");
        return cur;
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

        // Always try clipboard probe if FlaUI and Win32 methods failed
        // This ensures drag-selected text is captured even if AllowClipboardProbe is disabled
        // (AllowClipboardProbe only affects whether we probe when there's already text selected)
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

    public static string GetCurrentVersionString()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            // Prefer InformationalVersion if present (can include semver + metadata)
            var infoAttr = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
            {
                return infoAttr.InformationalVersion.Trim();
            }

            // Fall back to AssemblyFileVersion
            var fileAttr = (System.Reflection.AssemblyFileVersionAttribute?)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyFileVersionAttribute));
            if (fileAttr != null && !string.IsNullOrWhiteSpace(fileAttr.Version))
            {
                return fileAttr.Version.Trim();
            }

            var v = asm.GetName()?.Version;
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
        if (string.IsNullOrWhiteSpace(cfg.UpdateRepoOwner) || string.IsNullOrWhiteSpace(cfg.UpdateRepoName))
            return null;

        var url = $"https://api.github.com/repos/{cfg.UpdateRepoOwner.Trim()}/{cfg.UpdateRepoName.Trim()}/releases/latest";
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

        if (!string.IsNullOrWhiteSpace(cfg.LastNotifiedVersion) &&
            string.Equals(NormalizeVersionTag(cfg.LastNotifiedVersion!), NormalizeVersionTag(latest.Tag), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ShowReleaseNotesDialog(latest, isManual: false);

        cfg.LastNotifiedVersion = latest.Tag;
        SaveConfig(cfg);
    }

    public static async Task ManualCheckUpdateFromSettingsAsync()
    {
        try
        {
            var cfg = GetCurrentConfig();
            var http = _httpGlobal ?? CreateHttp(cfg);
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
                cfg.LastNotifiedVersion = latest.Tag;
                SaveConfig(cfg);
            }
            else
            {
                MessageBox.Show($"You're up to date.\nCurrent: {current}\nLatest: {latest.Tag}", "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Manual update check from Settings failed: " + ex.Message);
            MessageBox.Show("Update check failed: " + ex.Message, "ChatMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
// Settings UI (Tabbed)
// ===============================
#region Settings UI

public class ConfigForm : Form
{
    // Tabs
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(16, 8) };
    private readonly TabPage tabGeneral = new() { Text = "General", Padding = new Padding(16) };
    private readonly TabPage tabPrompts = new() { Text = "Prompts & Hotkeys", Padding = new Padding(16) };
    private readonly TabPage tabLLM = new() { Text = "LLM", Padding = new Padding(16) };
    private readonly TabPage tabNetwork = new() { Text = "Network", Padding = new Padding(16) };
    private readonly TabPage tabUpdates = new() { Text = "Updates", Padding = new Padding(16) };

    // General
    private readonly TextBox tbMainHotkey = new() { Width = 300 };
    private readonly CheckBox cbAllowClip = new() { Text = "Allow clipboard probe when text is selected", AutoSize = true };
    private readonly CheckBox cbTrayMode = new() { Text = "Start minimized to system tray", AutoSize = true };
    private readonly NumericUpDown nudTooltipStay = new() { Minimum = 1500, Maximum = 60000, Increment = 500, Width = 100 };
    private readonly NumericUpDown nudTooltipError = new() { Minimum = 1500, Maximum = 60000, Increment = 500, Width = 100 };

    // Prompts & Hotkeys
    private readonly Panel pnlScrollPrompts = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly List<TextBox> tbPrompts = new();
    private readonly List<TextBox> tbPromptHotkeys = new();

    // LLM
    private readonly TextBox tbBaseUrl = new() { Width = 500 };
    private readonly TextBox tbApiKey = new() { Width = 500, UseSystemPasswordChar = true };
    private readonly TextBox tbModel = new() { Width = 300 };
    private readonly TextBox tbCustomHeaders = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Height = 120, Width = 500 };
    private readonly Button btnShowApiKey = new() { Text = "Show", Width = 80 };

    // Network
    private readonly TextBox tbProxy = new() { Width = 400 };
    private readonly NumericUpDown nudTimeout = new() { Width = 100, Minimum = 1, Maximum = 300, DecimalPlaces = 0 };
    private readonly CheckBox cbSslOff = new() { Text = "Disable SSL certificate verification (not recommended)", AutoSize = true };

    // Updates
    private readonly CheckBox cbUpdate = new() { Text = "Automatically check for updates on startup", AutoSize = true };
    private readonly Button btnUpdateCheck = new() { Text = "Check Now", Width = 120, Height = 32 };
    private readonly TextBox tbCurrentVersion = new() { ReadOnly = true, Width = 200, BackColor = SystemColors.Control };
    private readonly TextBox tbRepoOwner = new() { ReadOnly = true, Width = 200, BackColor = SystemColors.Control };
    private readonly TextBox tbRepoName = new() { ReadOnly = true, Width = 200, BackColor = SystemColors.Control };

    private readonly Button btnOk = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
    private readonly Button btnCancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };

    private AppConfig _cfg;

    public ConfigForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "ChatMouse Settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true; MinimizeBox = true;
        Width = 1200; Height = 960;
        MinimumSize = new Size(1000, 800);

        // Build tabs
        BuildGeneralTab();
        BuildPromptsTab();
        BuildLLMTab();
        BuildNetworkTab();
        BuildUpdatesTab();

        // Bottom buttons panel with improved layout
        var btnPanel = new Panel
        {
            Height = 50,
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(240, 240, 240)
        };

        var btnLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        btnCancel.Margin = new Padding(0, 0, 8, 0);
        btnOk.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

        btnLayout.Controls.Add(btnCancel);
        btnLayout.Controls.Add(btnOk);
        btnPanel.Controls.Add(btnLayout);

        // Set default dialog buttons
        this.AcceptButton = btnOk;
        this.CancelButton = btnCancel;

        // Tab control
        tabs.TabPages.AddRange(new[] { tabGeneral, tabPrompts, tabLLM, tabNetwork, tabUpdates });

        // Important: add btnPanel after tabs so DockStyle.Bottom reserves space and buttons don't overlap
        Controls.Add(tabs);
        Controls.Add(btnPanel);

        // Fill values
        FillValuesFromConfig();

        btnOk.Click += (_, __) => { DialogResult = DialogResult.OK; Close(); };
        btnCancel.Click += (_, __) => { DialogResult = DialogResult.Cancel; Close(); };
    }

    private void BuildGeneralTab()
    {
        var scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        var flowLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Width = scrollPanel.ClientSize.Width - 25
        };

        // Hotkey Group
        var grpHotkey = new GroupBox
        {
            Text = "Hotkey Configuration",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = flowLayout.Width - 10,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10),
            Font = new Font("Segoe UI", 9.5f)
        };

        var tblHotkey = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2
        };
        tblHotkey.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        tblHotkey.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        tblHotkey.Controls.Add(new Label { Text = "Global Hotkey:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, 0);
        tbMainHotkey.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        tbMainHotkey.Margin = new Padding(3);
        tblHotkey.Controls.Add(tbMainHotkey, 1, 0);

        var lblHelp = new Label { Text = "Example: Ctrl+Shift+Space", AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(3) };
        tblHotkey.SetColumnSpan(lblHelp, 2);
        tblHotkey.Controls.Add(lblHelp, 0, 1);

        grpHotkey.Controls.Add(tblHotkey);

        // Tooltip Group
        var grpTooltip = new GroupBox
        {
            Text = "Tooltip Display Settings",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = flowLayout.Width - 10,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10),
            Font = new Font("Segoe UI", 9.5f)
        };

        var tblTooltip = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 2
        };
        tblTooltip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        tblTooltip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblTooltip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        tblTooltip.Controls.Add(new Label { Text = "Normal Display:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, 0);
        nudTooltipStay.Margin = new Padding(3);
        tblTooltip.Controls.Add(nudTooltipStay, 1, 0);
        tblTooltip.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 2, 0);

        tblTooltip.Controls.Add(new Label { Text = "Error Display:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, 1);
        nudTooltipError.Margin = new Padding(3);
        tblTooltip.Controls.Add(nudTooltipError, 1, 1);
        tblTooltip.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 2, 1);

        grpTooltip.Controls.Add(tblTooltip);

        // Behavior Group
        var grpBehavior = new GroupBox
        {
            Text = "Application Behavior",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = flowLayout.Width - 10,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10),
            Font = new Font("Segoe UI", 9.5f)
        };

        var flowBehavior = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        cbAllowClip.AutoSize = true;
        cbAllowClip.Margin = new Padding(3);
        cbTrayMode.AutoSize = true;
        cbTrayMode.Margin = new Padding(3);

        flowBehavior.Controls.Add(cbAllowClip);
        flowBehavior.Controls.Add(cbTrayMode);
        grpBehavior.Controls.Add(flowBehavior);

        flowLayout.Controls.Add(grpHotkey);
        flowLayout.Controls.Add(grpTooltip);
        flowLayout.Controls.Add(grpBehavior);

        scrollPanel.Controls.Add(flowLayout);
        tabGeneral.Controls.Add(scrollPanel);
    }

    private void BuildPromptsTab()
    {
        var scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        var flowLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Width = scrollPanel.ClientSize.Width - 25
        };

        var prompts = (_cfg.Prompts ?? Array.Empty<string>()).ToArray();
        if (prompts.Length < 9) Array.Resize(ref prompts, 9);
        var hks = (_cfg.PromptHotkeys ?? Array.Empty<string>()).ToArray();
        if (hks.Length < 9) Array.Resize(ref hks, 9);

        for (int i = 0; i < 9; i++)
        {
            var grpPrompt = new GroupBox
            {
                Text = $"Prompt {i + 1}",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = flowLayout.Width - 10,
                Padding = new Padding(10),
                Margin = new Padding(0, 0, 0, 10),
                Font = new Font("Segoe UI", 9f)
            };

            var tblPrompt = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2
            };
            tblPrompt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
            tblPrompt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            tblPrompt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tblPrompt.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));

            tblPrompt.Controls.Add(new Label { Text = "Prompt Text:", AutoSize = true, Margin = new Padding(3) }, 0, 0);
            tblPrompt.Controls.Add(new Label { Text = "Hotkey:", AutoSize = true, Margin = new Padding(3) }, 1, 0);

            var tbP = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Text = prompts[i] ?? "",
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                Margin = new Padding(3)
            };
            tbPrompts.Add(tbP);
            tblPrompt.Controls.Add(tbP, 0, 1);

            var tbH = new TextBox
            {
                Text = hks[i] ?? "",
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9f),
                Margin = new Padding(3)
            };
            tbPromptHotkeys.Add(tbH);
            tblPrompt.Controls.Add(tbH, 1, 1);

            grpPrompt.Controls.Add(tblPrompt);
            flowLayout.Controls.Add(grpPrompt);
        }

        scrollPanel.Controls.Add(flowLayout);
        tabPrompts.Controls.Add(scrollPanel);
    }

    private void BuildLLMTab()
    {
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        var containerPanel = new Panel
        {
            Location = new Point(8, 8),
            Width = 850,
            Height = 500
        };

        // API Configuration Group
        var grpApi = new GroupBox
        {
            Text = "API Configuration",
            Location = new Point(0, 0),
            Width = 850,
            Height = 180,
            Padding = new Padding(12),
            Font = new Font("Segoe UI", 9.5f)
        };

        // Base URL
        var lblBaseUrl = new Label
        {
            Text = "Base URL:",
            Location = new Point(15, 30),
            AutoSize = true
        };

        tbBaseUrl.Location = new Point(120, 27);

        // API Key
        var lblApiKey = new Label
        {
            Text = "API Key:",
            Location = new Point(15, 60),
            AutoSize = true
        };

        tbApiKey.Location = new Point(120, 57);

        btnShowApiKey.Location = new Point(625, 56);
        btnShowApiKey.Click += (_, __) =>
        {
            tbApiKey.UseSystemPasswordChar = !tbApiKey.UseSystemPasswordChar;
            btnShowApiKey.Text = tbApiKey.UseSystemPasswordChar ? "Show" : "Hide";
        };

        // Model
        var lblModel = new Label
        {
            Text = "Model:",
            Location = new Point(15, 90),
            AutoSize = true
        };

        tbModel.Location = new Point(120, 87);

        grpApi.Controls.Add(lblBaseUrl);
        grpApi.Controls.Add(tbBaseUrl);
        grpApi.Controls.Add(lblApiKey);
        grpApi.Controls.Add(tbApiKey);
        grpApi.Controls.Add(btnShowApiKey);
        grpApi.Controls.Add(lblModel);
        grpApi.Controls.Add(tbModel);

        // Custom Headers Group
        var grpHeaders = new GroupBox
        {
            Text = "Custom Headers (Optional)",
            Location = new Point(0, 190),
            Width = 850,
            Height = 180,
            Padding = new Padding(12),
            Font = new Font("Segoe UI", 9.5f)
        };

        var lblHeadersHelp = new Label
        {
            Text = "Enter custom headers as JSON object or \"Header: Value\" format (one per line)",
            Location = new Point(15, 25),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f)
        };

        tbCustomHeaders.Location = new Point(15, 45);
        tbCustomHeaders.Font = new Font("Consolas", 9f);

        grpHeaders.Controls.Add(lblHeadersHelp);
        grpHeaders.Controls.Add(tbCustomHeaders);

        containerPanel.Controls.Add(grpApi);
        containerPanel.Controls.Add(grpHeaders);

        scrollPanel.Controls.Add(containerPanel);
        tabLLM.Controls.Add(scrollPanel);
    }

    private void BuildNetworkTab()
    {
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        var containerPanel = new Panel
        {
            Location = new Point(8, 8),
            Width = 850,
            Height = 400
        };

        // Network Settings Group
        var grpNetwork = new GroupBox
        {
            Text = "Network Configuration",
            Location = new Point(0, 0),
            Width = 850,
            Height = 150,
            Padding = new Padding(12),
            Font = new Font("Segoe UI", 9.5f)
        };

        // HTTP Proxy
        var lblProxy = new Label
        {
            Text = "HTTP Proxy:",
            Location = new Point(15, 30),
            AutoSize = true
        };

        tbProxy.Location = new Point(150, 27);

        var lblProxyHelp = new Label
        {
            Text = "Format: http://proxy:port",
            Location = new Point(555, 30),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f)
        };

        // Timeout
        var lblTimeout = new Label
        {
            Text = "Request Timeout:",
            Location = new Point(15, 60),
            AutoSize = true
        };

        nudTimeout.Location = new Point(150, 57);

        var lblTimeoutUnit = new Label
        {
            Text = "seconds",
            Location = new Point(255, 60),
            AutoSize = true
        };

        // SSL checkbox
        cbSslOff.Location = new Point(15, 90);

        grpNetwork.Controls.Add(lblProxy);
        grpNetwork.Controls.Add(tbProxy);
        grpNetwork.Controls.Add(lblProxyHelp);
        grpNetwork.Controls.Add(lblTimeout);
        grpNetwork.Controls.Add(nudTimeout);
        grpNetwork.Controls.Add(lblTimeoutUnit);
        grpNetwork.Controls.Add(cbSslOff);

        containerPanel.Controls.Add(grpNetwork);

        scrollPanel.Controls.Add(containerPanel);
        tabNetwork.Controls.Add(scrollPanel);
    }

    private void BuildUpdatesTab()
    {
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        var containerPanel = new Panel
        {
            Location = new Point(8, 8),
            Width = 850,
            Height = 400
        };

        // Update Actions Group
        var grpActions = new GroupBox
        {
            Text = "Update Management",
            Location = new Point(0, 0),
            Width = 850,
            Height = 100,
            Padding = new Padding(12),
            Font = new Font("Segoe UI", 9.5f)
        };

        btnUpdateCheck.Location = new Point(15, 30);
        btnUpdateCheck.Font = new Font("Segoe UI", 9.5f);
        btnUpdateCheck.ForeColor = Color.FromArgb(0, 100, 200);
        btnUpdateCheck.Click += async (_, __) => { try { await App.ManualCheckUpdateFromSettingsAsync(); } catch { } };

        cbUpdate.Location = new Point(15, 65);

        grpActions.Controls.Add(btnUpdateCheck);
        grpActions.Controls.Add(cbUpdate);

        // Version Information Group
        var grpVersion = new GroupBox
        {
            Text = "Version Information",
            Location = new Point(0, 110),
            Width = 850,
            Height = 130,
            Padding = new Padding(12),
            Font = new Font("Segoe UI", 9.5f)
        };

        var lblCurrentVersion = new Label
        {
            Text = "Current Version:",
            Location = new Point(15, 30),
            AutoSize = true
        };

        tbCurrentVersion.Location = new Point(150, 27);
        tbCurrentVersion.Font = new Font("Segoe UI", 9f);

        var lblRepoOwner = new Label
        {
            Text = "Repository Owner:",
            Location = new Point(15, 60),
            AutoSize = true
        };

        tbRepoOwner.Location = new Point(150, 57);
        tbRepoOwner.Font = new Font("Segoe UI", 9f);

        var lblRepoName = new Label
        {
            Text = "Repository Name:",
            Location = new Point(15, 90),
            AutoSize = true
        };

        tbRepoName.Location = new Point(150, 87);
        tbRepoName.Font = new Font("Segoe UI", 9f);

        grpVersion.Controls.Add(lblCurrentVersion);
        grpVersion.Controls.Add(tbCurrentVersion);
        grpVersion.Controls.Add(lblRepoOwner);
        grpVersion.Controls.Add(tbRepoOwner);
        grpVersion.Controls.Add(lblRepoName);
        grpVersion.Controls.Add(tbRepoName);

        containerPanel.Controls.Add(grpActions);
        containerPanel.Controls.Add(grpVersion);

        scrollPanel.Controls.Add(containerPanel);
        tabUpdates.Controls.Add(scrollPanel);
    }

    private static TableLayoutPanel NewTable(int cols)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = cols,
            AutoSize = false
        };
        if (cols == 2)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }
        else
        {
            for (int i = 0; i < cols; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        }
        return layout;
    }

    private static void AddRow2(TableLayoutPanel layout, ref int row, string label, Control ctrl)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 6, 0) }, 0, row);
        layout.Controls.Add(ctrl, 1, row);
        row++;
    }

    // Add a single control that should span both columns (useful for long CheckBox text)
    private static void AddRowSpan2(TableLayoutPanel layout, ref int row, Control ctrl)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ctrl.Anchor = AnchorStyles.Left;
        ctrl.AutoSize = true;
        layout.Controls.Add(ctrl, 0, row);
        layout.SetColumnSpan(ctrl, 2);
        row++;
    }

    private void FillValuesFromConfig()
    {
        // General
        tbMainHotkey.Text = _cfg.Hotkey;
        cbAllowClip.Checked = _cfg.AllowClipboardProbe;
        cbTrayMode.Checked = _cfg.TrayMode;
        nudTooltipStay.Value = Math.Max(1500, _cfg.TooltipStayMs);
        nudTooltipError.Value = Math.Max(1500, _cfg.TooltipErrorMs);

        // LLM
        tbBaseUrl.Text = _cfg.BaseUrl;
        tbApiKey.Text = _cfg.ApiKey;
        tbModel.Text = _cfg.Model;
        // JSON 형식으로 표시 (더 읽기 쉬움)
        var headers = _cfg.LlmCustomHeaders ?? Array.Empty<string>();
        if (headers.Length > 0)
        {
            try
            {
                // 기존 형식인지 JSON 형식인지 확인
                string combined = string.Join("", headers.Select(l => (l ?? "").Trim()));
                if (combined.StartsWith("{") && combined.EndsWith("}"))
                {
                    tbCustomHeaders.Text = combined;
                }
                else
                {
                    // "Header: Value" 형식을 JSON으로 변환하여 표시
                    var dict = new Dictionary<string, string>();
                    foreach (var raw in headers)
                    {
                        var s = (raw ?? "").Trim();
                        if (s.Length == 0) continue;
                        int p = s.IndexOf(':');
                        if (p <= 0) continue;
                        var key = s.Substring(0, p).Trim();
                        var val = s.Substring(p + 1).Trim();
                        if (key.Length > 0) dict[key] = val;
                    }
                    if (dict.Count > 0)
                    {
                        tbCustomHeaders.Text = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                    }
                    else
                    {
                        tbCustomHeaders.Text = string.Join(Environment.NewLine, headers);
                    }
                }
            }
            catch
            {
                tbCustomHeaders.Text = string.Join(Environment.NewLine, headers);
            }
        }
        else
        {
            tbCustomHeaders.Text = "";
        }

        // Network
        tbProxy.Text = _cfg.HttpProxy ?? "";
        nudTimeout.Value = _cfg.RequestTimeoutSeconds > 0 ? _cfg.RequestTimeoutSeconds : 10;
        cbSslOff.Checked = _cfg.DisableSslVerify;

        // Updates
        cbUpdate.Checked = _cfg.UpdateCheckEnabled;
        tbCurrentVersion.Text = App.GetCurrentVersionString();
        tbRepoOwner.Text = _cfg.UpdateRepoOwner ?? "";
        tbRepoName.Text = _cfg.UpdateRepoName ?? "";
    }

    public AppConfig GetConfig()
    {
        var prompts = tbPrompts.Select(tb => (tb.Text ?? "").Trim()).ToArray();
        if (prompts.Length < 9) Array.Resize(ref prompts, 9);

        var hotkeys = tbPromptHotkeys.Select(tb => (tb.Text ?? "").Trim()).ToArray();
        if (hotkeys.Length < 9) Array.Resize(ref hotkeys, 9);

        // JSON 형식 또는 "Header: Value" 형식 모두 지원
        string headersText = (tbCustomHeaders.Text ?? "").Trim();
        string[] headers;
        
        if (headersText.StartsWith("{") && headersText.EndsWith("}"))
        {
            // JSON 형식: 파싱하여 "Header: Value" 형식으로 변환
            try
            {
                var jsonOpts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var jsonDict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersText, jsonOpts);
                if (jsonDict != null && jsonDict.Count > 0)
                {
                    headers = jsonDict.Select(kv => $"{kv.Key}: {kv.Value}").ToArray();
                }
                else
                {
                    headers = Array.Empty<string>();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to parse custom headers JSON: {ex.Message}");
                // JSON 파싱 실패 시 빈 배열로 처리
                headers = Array.Empty<string>();
            }
        }
        else
        {
            // 기존 "Header: Value" 형식
            headers = headersText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        return new AppConfig
        {
            // General
            Hotkey = tbMainHotkey.Text.Trim(),
            AllowClipboardProbe = cbAllowClip.Checked,
            TrayMode = cbTrayMode.Checked,
            TooltipStayMs = (int)nudTooltipStay.Value,
            TooltipErrorMs = (int)nudTooltipError.Value,

            // Prompts
            Prompt = prompts[0] ?? "",
            Prompts = prompts,
            PromptHotkeys = hotkeys,

            // LLM
            BaseUrl = tbBaseUrl.Text.Trim(),
            ApiKey = tbApiKey.Text.Trim(),
            Model = tbModel.Text.Trim(),
            LlmCustomHeaders = headers,

            // Network
            HttpProxy = string.IsNullOrWhiteSpace(tbProxy.Text) ? null : tbProxy.Text.Trim(),
            DisableSslVerify = cbSslOff.Checked,
            RequestTimeoutSeconds = (int)nudTimeout.Value,

            // Updates
            UpdateCheckEnabled = cbUpdate.Checked,
            UpdateRepoOwner = _cfg.UpdateRepoOwner, // read-only here
            UpdateRepoName = _cfg.UpdateRepoName,
            LastNotifiedVersion = App.GetCurrentConfig().LastNotifiedVersion
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
