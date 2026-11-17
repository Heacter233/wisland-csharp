using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wisland.Controls;
using Wisland.Pages;
using Wisland.Services;
using Wisland.Theme;
using Glyph = Wisland.Theme.Glyph;
using MediaState = Wisland.Services.MediaState;
using WeatherState = Wisland.Services.WeatherState;

namespace Wisland;

/// <summary>
/// 主窗口：灵动岛外壳（对应 Python ui/island.py + app.py）。
/// - 悬停展开 / 收起（弹簧尺寸动画）
/// - 导航栏切换 6 个页面
/// - 收起态绘制（时钟/天气/媒体/形象/活动/字幕）
/// - 托盘菜单、全局热键、贴边锚定、主题切换
/// </summary>
public partial class MainWindow : Window
{
    // ---------------- P/Invoke ----------------
    private const int WmHotkey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int WmTrayIcon = 0x0400;

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public IntPtr dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        public uint dwState;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szInfoTitle;
    }

    private const int NimAdd = 0;
    private const int NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;

    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;   // 保留常量但不再使用（输入框需要窗口可激活）

    // ---------------- UI ----------------
    private readonly CompactView _compact = new();
    private IslandChrome _chrome = null!;
    private NavBar _nav = null!;
    private ContentControl _pageHost = null!;
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly Dictionary<string, double> _pageHeights = new()
    {
        ["home"] = 240, ["msg"] = 270, ["weather"] = 318,
        ["ctl"] = 180, ["clip"] = 400, ["trans"] = 330,
    };
    private string _currentPage = "home";

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private IntPtr _hwnd;
    private string _edge = "top";
    private double _pos = 0.5;
    private bool _trayMenuOpen;
    private readonly HashSet<string> _uiLocks = new();
    private bool _passthrough;

    private Backdrop? _backdrop;
    private DateTime _lastFrame = DateTime.UtcNow;
    private DateTime _lastBackdropRequest = DateTime.MinValue;
    private DateTime _lastGlassRefresh = DateTime.MinValue;
    private bool _renderingHooked;

    public MainWindow()
    {
        InitializeComponent();
        BuildUi();
        Loaded += (_, _) => { StartTicker(); _clockTimer.Start(); };
    }

    // ---------------- UI 构建 ----------------
    private void BuildUi()
    {
        // 展开内容：导航 + 页面
        _nav = new NavBar(new (string, string, string)[]
        {
            ("home", Glyph.Home, "主页"),
            ("msg", Glyph.Message, "消息"),
            ("weather", Glyph.Cloud, "天气"),
            ("ctl", Glyph.Sliders, "控制"),
            ("clip", Glyph.Clip, "剪贴板"),
            ("trans", Glyph.Translate, "翻译"),
            ("set", Glyph.Gear, "设置"),
        });
        _nav.SelectionChanged += (_, key) => ShowPage(key);
        _nav.DragRequested += (_, _) => BeginDrag();

        _pageHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_nav, 0);
        Grid.SetRow(_pageHost, 2);
        inner.Children.Add(_nav);
        inner.Children.Add(_pageHost);

        var host = new Border
        {
            Padding = new Thickness(12, 10, 12, 12),
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Child = inner,
            Opacity = 0,
        };

        _chrome = new IslandChrome(_compact, host) { AnchorEdge = _edge };
        _backdrop = new Backdrop();
        _chrome.Backdrop = _backdrop;
        _chrome.ExpandedHeight = () => CurrentPageHeight() + 66;
        _chrome.DragRequested += (_, _) => BeginDrag();
        _chrome.PlayPauseRequested += (_, _) => ServiceHub.Media.SetPlayPause();
        _chrome.ViewCycled += (_, d) => CycleCompactView(d);

        RootGrid.Children.Add(_chrome);

        // 滚动时 WPF 会对整窗做位图平移优化，手绘玻璃层会被一起平移，
        // 表现为"毛玻璃里出现滚动残影"。订阅路由的 ScrollChanged 强制重绘玻璃层。
        // 滚动时 WPF 的像素滚动不会清掉旧位置的像素，表现为"滚动残影"。
        // 对滚动内容做一次子树重绘即可消除。
        AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, e) =>
            {
                if (e.OriginalSource is System.Windows.Controls.ScrollViewer sv)
                {
                    if (sv != _scrollPending)
                    {
                        _scrollPending = sv;
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                            new Action(() =>
                            {
                                var target = _scrollPending;
                                _scrollPending = null;
                                if (target != null) InvalidateSubtree(target);
                            }));
                    }
                }
                _chrome.InvalidateVisual();
            }), handledEventsToo: true);

        PreviewMouseLeftButtonDown += (_, _) => { };

        _pages["home"] = new HomePage();
        _pages["msg"] = new MessagesPage();
        _pages["weather"] = new WeatherPage();
        _pages["ctl"] = new ControlsPage();
        _pages["clip"] = new ClipboardPage();
        _pages["trans"] = new TranslatePage();
        _pages["set"] = new SettingsPage();
        ShowPage("home", force: true);
        _chrome.SnapSize();

        // 固定窗口尺寸：动画中不再改窗口大小（改尺寸会重合成整窗 → 闪烁/卡顿）
        Width = FixedWindowWidth;
        Height = FixedWindowHeight;
    }

    /// <summary>当前页面要求的高度（页面自身 Height 优先）。</summary>
    private double CurrentPageHeight()
    {
        if (_pages.TryGetValue(_currentPage, out var page) && page.Height > 10) return page.Height;
        return _pageHeights.GetValueOrDefault(_currentPage, 300);
    }

    private void ShowPage(string key, bool force = false)
    {
        if (!_pages.ContainsKey(key)) return;
        if (!force && key == _currentPage) return;
        _currentPage = key;
        _pageHost.Content = _pages[key];
        _nav.SetCurrent(key);
        if (_chrome.IsExpanded)
        {
            _chrome.RetargetExpandedHeight(CurrentPageHeight() + 66);
        }
        ServiceHub.Config.Set("island.last_tab", key, save: false);
    }

    private void CycleCompactView(int d)
    {
        var v = _compact.CycleView(d);
        ServiceHub.Config.Set("island.compact_view", v, save: false);
        _chrome.RetargetCompact();
    }

    // ---------------- 启动 ----------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // 允许窗口被激活（输入框需要键盘焦点）
        if (!_chrome.IsExpanded) { base.OnMouseLeftButtonDown(e); return; }
        try
        {
            if (!IsActive) Activate();
        }
        catch { }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(WndProc);

        // 工具窗口 + 不激活（不占 Alt+Tab）
        var ex = GetWindowLong(_hwnd, GwlExstyle);
        // 只保留工具窗口（不进 Alt+Tab）；不能用 NOACTIVATE，否则输入框无法获得键盘焦点
        SetWindowLong(_hwnd, GwlExstyle, (ex | WsExToolWindow) & ~WsExNoActivate);
        _backdrop?.Attach(_hwnd);

        Try("hotkey", () =>
        {
            ServiceHub.Hotkey.Initialize(_hwnd);
            ServiceHub.Hotkey.HotkeyPressed += OnHotkey;
            ReloadHotkeys();
        });

        Try("tray", InitializeTray);

        Try("clipboard", () =>
        {
            ServiceHub.Clipboard.Initialize(_hwnd);
            ServiceHub.Clipboard.Start();
        });

        Try("notify", () => ServiceHub.Notify.Start());
        Try("radio", () => ServiceHub.Radio.Start());

        Try("media", () => ServiceHub.Media.Initialize(_hwnd));
        Try("media-events", () => ServiceHub.Media.Changed += Media_Changed);
        Try("weather", () =>
        {
            ServiceHub.Weather.Updated += Weather_Updated;
            ServiceHub.Weather.Initialize(
                city: ServiceHub.Config.Get("weather.city", "上海"),
                apiKey: ServiceHub.Config.Get("weather.api_key", ""),
                units: ServiceHub.Config.Get("weather.units", "metric"),
                lang: ServiceHub.Config.Get("weather.lang", "zh_cn"),
                intervalMinutes: ServiceHub.Config.Get("weather.interval_min", 15),
                useOpenMeteo: string.IsNullOrEmpty(ServiceHub.Config.Get("weather.api_key", "")));
            var st = ServiceHub.Weather.State;
            _compact.Weather = st;
        });
        Try("live", () =>
        {
            ServiceHub.Live.Initialize(
                ServiceHub.Config.Get("live.models_dir", "models"),
                ServiceHub.Config.Get("live.audio.source", "loopback"),
                ServiceHub.Config.Get("live.audio.device", ""),
                ServiceHub.Config.Get("live.audio.block_ms", 80));
            ServiceHub.Live.RunningChanged += (_, on) => Dispatcher.Invoke(() => _compact.SetSubRunning(on));
            ServiceHub.Live.Partial += (_, jp) => Dispatcher.Invoke(() =>
            {
                _compact.SetSub(jp: jp);
                _chrome.RetargetCompact();
            });
            ServiceHub.Live.Segment += (_, s) => Dispatcher.Invoke(() =>
            {
                _compact.SetSub(jp: s.jp);
                _chrome.RetargetCompact();
            });
            ServiceHub.Live.LiveZh += (_, s) => Dispatcher.Invoke(() =>
            {
                _compact.SetSub(zh: s.zh);
                _chrome.RetargetCompact();
            });
            ServiceHub.Live.FinalZh += (_, s) => Dispatcher.Invoke(() => _compact.SetSub(zh: s.zh));
            ServiceHub.Live.Level += (_, v) => { };
        });

        Try("anchor", () =>
        {
            _edge = ServiceHub.Config.Get("island.edge", "top");
            _pos = ServiceHub.Config.Get("island.pos", 0.5);
            ApplyAnchor();
        });

        Try("theme", () =>
        {
            var mode = ServiceHub.Config.Get("island.theme", "auto");
            ThemeApplier.ApplyMode(Application.Current.Resources, mode);
            Themes.Changed += (_, _) =>
            {
                ThemeApplier.Apply(Application.Current.Resources);
                _compact.InvalidateVisual();
                ServiceHub.RaiseThemeChanged();
            };
            ServiceHub.RequestThemeRefresh = () => ThemeApplier.Apply(Application.Current.Resources);
        });

        Try("restore", () =>
        {
            var view = ServiceHub.Config.Get("island.compact_view", "clock");
            _compact.View = view;
            if (ServiceHub.Config.Get("island.pinned", false) ||
                ServiceHub.Config.Get("island.expand_mode", "hover") == "always")
                Expand();
        });

        Try("hooks", () =>
        {
            ServiceHub.RequestExpand = key => { if (key.Length > 0) ShowPage(key); Expand(); };
            ServiceHub.RequestCollapse = Collapse;
            ServiceHub.RequestReanchor = PositionWindow;
            ServiceHub.RequestTimings = ApplyTimings;
            ServiceHub.RequestPassthrough = SetPassthrough;
            ServiceHub.RequestAutostart = SetAutoStart;
            ServiceHub.RequestCompactView = v => { _compact.ForceView(v); _chrome.RetargetCompact(); };
            ServiceHub.RequestReloadHotkeys = ReloadHotkeys;
            ServiceHub.PushUiLock = key => { _uiLocks.Add(key); _collapseTimer.Stop(); };
            ServiceHub.PopUiLock = key => { _uiLocks.Remove(key); };
            ServiceHub.RequestRetargetHeight = () =>
            {
                if (_chrome.IsExpanded) _chrome.RetargetExpandedHeight(CurrentPageHeight() + 66);
                else _chrome.RetargetCompact();
            };
        });

        Try("debugpage", () =>
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--page:", StringComparison.OrdinalIgnoreCase));
            if (arg != null)
            {
                var key = arg["--page:".Length..];
                if (_pages.ContainsKey(key))
                {
                    ShowPage(key);
                    Expand();
                }
            }
        });

        Try("autostart", () =>
        {
            if (ServiceHub.Config.Get("island.autostart", false)) SetAutoStart(true);
        });

        Try("passthrough", () =>
        {
            if (ServiceHub.Config.Get("island.passthrough", false)) SetPassthrough(true);
        });
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _collapseTimer.Stop();
        if (_chrome.IsExpanded || _passthrough) return;
        var mode = ServiceHub.Config.Get("island.expand_mode", "hover");
        if (mode == "never") return;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTimer.Stop();
        if (!_chrome.IsExpanded || _passthrough) return;
        if (ServiceHub.Config.Get("island.pinned", false)) return;
        // 菜单/对话框打开期间不收
        if (_uiLocks.Count > 0) return;
        // 正在输入（输入框有键盘焦点）不收
        if (IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void StartTicker()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Expand(); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); Collapse(); };

        ApplyTimings();
        _clockTimer.Tick += (_, _) => Clock_Tick();

        CompositionTarget.Rendering += OnFrame;
    }

    private void Clock_Tick()
    {
        // 时钟每秒重绘（同时驱动字幕保留期到期）
        _compact.InvalidateVisual();
        _chrome.RetargetCompact();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.05, Math.Max(0.001, (now - _lastFrame).TotalSeconds));
        _lastFrame = now;

        var moving = _chrome.Step(dt);
        var navMoving = _nav.Step(dt);

        // 光标位置 → 收起态形变与形象视线
        var mouse = Mouse.GetPosition(_chrome);
        var body = _chrome.BodyRect;
        var inside = body.Contains(mouse);
        _chrome.UpdatePoke(mouse, inside && !_chrome.IsExpanded);

        if (!_chrome.IsExpanded)
        {
            _compact.LookX = mouse.X - (body.X + body.Width / 2);
            _compact.LookY = mouse.Y - (body.Y + body.Height / 2);
            _compact.Near = inside;
            _compact.Peak = ServiceHub.Audio?.Peak ?? 0;

            // 播放按钮的悬停态
            if (_compact.PlayRect is { } pr)
            {
                var local = new Rect(pr.X + body.X, pr.Y + body.Y, pr.Width, pr.Height);
                var target = local.Contains(mouse) ? 1.0 : 0.0;
                _compact.PlayHover += (target - _compact.PlayHover) * 0.4;
            }
            var busy = _compact.Busy;
            if (busy || moving || navMoving || Math.Abs(_compact.PlayHover) > 0.01 || _compact.Near)
                _compact.InvalidateVisual();
        }

        // 背景模糊：窗口位置/尺寸不变时不必逐帧抓屏
        if (_backdrop != null && (now - _lastBackdropRequest).TotalMilliseconds > 220)
        {
            _lastBackdropRequest = now;
            // 展开态每秒强制重新抓一次：内容变化（滚动/切换）后玻璃底能及时刷新
            if (_chrome.IsExpanded && (now - _lastGlassRefresh).TotalMilliseconds > 1000)
            {
                _lastGlassRefresh = now;
                _backdrop.Invalidate();
            }
            _backdrop.Request(ActualWidth > 1 ? ActualWidth : Width, ActualHeight > 1 ? ActualHeight : Height);
        }
    }

    // ---------------- 展开 / 收起 ----------------
    public void Expand()
    {
        if (_chrome.IsExpanded) return;
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _chrome.ExpandedHeight = () => CurrentPageHeight() + 66;
        _chrome.Expand();
        // 首次展开时页面可能还没被测量，强制走一次布局，避免空白
        _pageHost.InvalidateMeasure();
        _pageHost.UpdateLayout();
        Dispatcher.BeginInvoke(new Action(() => _pageHost.UpdateLayout()),
            System.Windows.Threading.DispatcherPriority.Loaded);
        // 展开后允许键盘输入（输入框需要窗口激活）
        try { Activate(); } catch { }

        var page = _pages.TryGetValue(_currentPage, out var p) ? p : null;
        if (page is ClipboardPage cp) cp.Refresh();
        if (page is MessagesPage mp) mp.Refresh();
        if (page is TranslatePage tp && ServiceHub.Live.Running) tp.ShowLive();
    }

    public void Collapse()
    {
        if (!_chrome.IsExpanded) return;
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _chrome.Collapse();
    }

    public void ToggleIsland()
    {
        if (_chrome.IsExpanded)
        {
            if (ServiceHub.Config.Get("island.pinned", false))
                ServiceHub.Config.Set("island.pinned", false);
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    // ---------------- 窗口拖动 ----------------
    private void BeginDrag()
    {
        try { DragMove(); }
        catch { }
        // 拖动结束后按最近边吸附
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var bounds = WorkAreaDip();
            var body = BodyScreenRect();
            var gaps = new Dictionary<string, double>
            {
                ["top"] = Math.Abs(body.Top - bounds.Top),
                ["bottom"] = Math.Abs(bounds.Bottom - body.Bottom),
                ["left"] = Math.Abs(body.Left - bounds.Left),
                ["right"] = Math.Abs(bounds.Right - body.Right),
            };
            _edge = gaps.OrderBy(kv => kv.Value).First().Key;
            var horiz = _edge is "top" or "bottom";
            _pos = horiz
                ? Math.Clamp(((body.Left + body.Width / 2) - bounds.Left) / bounds.Width, 0, 1)
                : Math.Clamp(((body.Top + body.Height / 2) - bounds.Top) / bounds.Height, 0, 1);
            ServiceHub.Config.Set("island.edge", _edge);
            ServiceHub.Config.Set("island.pos", Math.Round(_pos, 4));
            _chrome.AnchorEdge = _edge;
            PositionWindow();
        }));
    }

    private Rect BodyScreenRect()
    {
        var body = _chrome.BodyRect;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            Left + body.X,
            Top + body.Y,
            body.Width,
            body.Height);
    }

    private Rect WorkAreaDip()
    {
        var bounds = GetPrimaryWorkAreaPx();
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Rect(bounds.Left / sx, bounds.Top / sy, bounds.Width / sx, bounds.Height / sy);
    }

    private struct RectPx
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private static RectPx GetPrimaryWorkAreaPx()
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (GetMonitorInfo(MonitorFromPoint(new POINT { X = 0, Y = 0 }, 2), ref mi) &&
            mi.rcWork.Right > mi.rcWork.Left)
            return new RectPx
            {
                Left = mi.rcWork.Left, Top = mi.rcWork.Top,
                Right = mi.rcWork.Right, Bottom = mi.rcWork.Bottom,
            };
        return new RectPx { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>固定窗口尺寸（容纳最大展开态 + 阴影余量）。</summary>
    public const double FixedWindowWidth = 700;
    public const double FixedWindowHeight = 540;

    /// <summary>按 island.edge / island.pos 摆放窗口，使「本体」贴住对应屏幕边。</summary>
    private void PositionWindow()
    {
        var wa = WorkAreaDip();
        var pad = IslandChrome.WindowPad;
        var (bw, bh) = _chrome.BodySize;
        var winW = Width > 1 ? Width : FixedWindowWidth;
        var winH = Height > 1 ? Height : FixedWindowHeight;

        // 本体在窗口内的偏移（与 IslandChrome.ArrangeOverride 保持一致）
        double bodyOffX, bodyOffY;
        switch (_edge)
        {
            case "bottom":
                bodyOffX = (winW - bw) / 2;
                bodyOffY = winH - pad - bh;
                break;
            case "left":
                bodyOffX = pad;
                bodyOffY = (winH - bh) / 2;
                break;
            case "right":
                bodyOffX = winW - pad - bw;
                bodyOffY = (winH - bh) / 2;
                break;
            default:
                bodyOffX = (winW - bw) / 2;
                bodyOffY = pad;
                break;
        }

        // 期望的本体屏幕位置
        double bodyLeft, bodyTop;
        switch (_edge)
        {
            case "bottom":
                bodyLeft = wa.Left + wa.Width * _pos - bw / 2;
                bodyTop = wa.Bottom - bh;
                break;
            case "left":
                bodyLeft = wa.Left;
                bodyTop = wa.Top + wa.Height * _pos - bh / 2;
                break;
            case "right":
                bodyLeft = wa.Right - bw;
                bodyTop = wa.Top + wa.Height * _pos - bh / 2;
                break;
            default:
                bodyLeft = wa.Left + wa.Width * _pos - bw / 2;
                bodyTop = wa.Top;
                break;
        }

        Left = Math.Round(bodyLeft - bodyOffX);
        Top = Math.Round(bodyTop - bodyOffY);
    }
    /// <summary>兼容旧调用名。</summary>
    private void ApplyAnchor() => PositionWindow();

    // ---------------- 托盘 ----------------
    private void InitializeTray()
    {
        var icon = LoadAppIcon();
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = WmTrayIcon,
            szTip = "wisland 灵动岛",
            hIcon = icon,
        };
        Shell_NotifyIcon(NimAdd, ref nid);
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "1002.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile);
                if (h != IntPtr.Zero) return h;
            }
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Wisland.exe");
            var h2 = ExtractIcon(IntPtr.Zero, exe, 0);
            if (h2 != IntPtr.Zero && h2.ToInt64() != 1) return h2;
        }
        catch { }
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        var menu = BuildContextMenu();
        _trayMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _trayMenuOpen = false;
            if (!IsMouseOver && _chrome.IsExpanded && !ServiceHub.Config.Get("island.pinned", false))
                Collapse();
        };
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private ContextMenu BuildContextMenu()
    {
        var cfg = ServiceHub.Config;
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Themes.Current.MenuBg),
            Foreground = new SolidColorBrush(Themes.Current.MenuFg),
            BorderBrush = new SolidColorBrush(Themes.Current.MenuBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            FontSize = 13,
        };

        MenuItem Item(string header, Action action, bool check = false, bool? isChecked = null)
        {
            var mi = new MenuItem { Header = header, IsCheckable = false };
            if (isChecked is { } c) mi.Header = (c ? "✓  " : "    ") + header;
            mi.Click += (_, _) => { action(); };
            mi.Foreground = new SolidColorBrush(Themes.Current.MenuFg);
            return mi;
        }

        menu.Items.Add(Item("展开 / 收起", ToggleIsland));
        menu.Items.Add(Item("编辑配置 config.json", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cfg.ConfigPath) { UseShellExecute = true }); }
            catch { }
        }));
        menu.Items.Add(Item("重新加载配置", () =>
        {
            cfg.Reload();
            ThemeApplier.ApplyMode(Application.Current.Resources, cfg.Get("island.theme", "auto"));
            _compact.InvalidateVisual();
        }));

        var themeMenu = new MenuItem { Header = "主题" };
        foreach (var (mode, label) in Themes.Names)
        {
            var cur = cfg.Get("island.theme", "auto");
            themeMenu.Items.Add(Item(label, () =>
            {
                cfg.Set("island.theme", mode);
                ThemeApplier.ApplyMode(Application.Current.Resources, mode);
                _compact.InvalidateVisual();
                ServiceHub.RaiseThemeChanged();
            }, isChecked: cur == mode));
        }
        menu.Items.Add(themeMenu);

        var modeMenu = new MenuItem { Header = "展开方式" };
        foreach (var (mode, label) in new[] { ("hover", "悬停展开"), ("always", "常驻展开"), ("never", "长关") })
        {
            var cur = cfg.Get("island.expand_mode", "hover");
            modeMenu.Items.Add(Item(label, () =>
            {
                cfg.Set("island.expand_mode", mode);
                if (mode == "always") Expand();
                if (mode == "hover") Collapse();
            }, isChecked: cur == mode));
        }
        menu.Items.Add(modeMenu);

        menu.Items.Add(Item("可穿透", () =>
        {
            SetPassthrough(!_passthrough);
            cfg.Set("island.passthrough", _passthrough);
        }, isChecked: _passthrough));

        menu.Items.Add(Item("开机自启", () =>
        {
            var on = !cfg.Get("island.autostart", false);
            cfg.Set("island.autostart", on);
            SetAutoStart(on);
        }, isChecked: cfg.Get("island.autostart", false)));

        menu.Items.Add(Item("打开数据目录", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cfg.DataDir) { UseShellExecute = true }); }
            catch { }
        }));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出 wisland", () => Application.Current.Shutdown()));

        return menu;
    }

    private void SetPassthrough(bool on)
    {
        _passthrough = on;
        var ex = GetWindowLong(_hwnd, GwlExstyle);
        SetWindowLong(_hwnd, GwlExstyle, on ? (ex | WsExTransparent) : (ex & ~WsExTransparent));
        if (on)
        {
            _compact.View = "pet";
            _chrome.RetargetCompact();
        }
    }

    private static void SetAutoStart(bool enable)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath ??
                          Path.Combine(AppContext.BaseDirectory, "Wisland.exe");
                key.SetValue("wisland", $"\"{exe}\" --startup");
            }
            else key.DeleteValue("wisland", false);
        }
        catch { }
    }

    // ---------------- 消息 ----------------
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            ServiceHub.Hotkey.OnHotkey(wParam);
            handled = true;
        }
        else if (msg == WmClipboardUpdate)
        {
            ServiceHub.Clipboard.OnClipboardUpdated();
            handled = true;
        }
        else if (msg == WmTrayIcon)
        {
            var ev = lParam.ToInt32() & 0xFFFF;
            // WM_RBUTTONUP = 0x0205, WM_LBUTTONUP = 0x0202
            if (ev is 0x0205 or 0x0204) ShowTrayMenu();
            else if (ev is 0x0202 or 0x0203) ToggleIsland();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private System.Windows.Controls.ScrollViewer? _scrollPending;

    /// <summary>递归标记重绘（用于消除滚动残影）。</summary>
    private static void InvalidateSubtree(DependencyObject root)
    {
        if (root is UIElement el) el.InvalidateVisual();
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++) InvalidateSubtree(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>从配置应用悬停/收起延时。</summary>
    public void ApplyTimings()
    {
        try
        {
            var h = ServiceHub.Config.Get("island.hover_delay_ms", 220);
            var c = ServiceHub.Config.Get("island.collapse_delay_ms", 450);
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(h, 0, 2000));
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(c, 50, 5000));
        }
        catch { }
    }

    /// <summary>按当前配置重新注册全局热键。</summary>
    private void ReloadHotkeys()
    {
        try
        {
            ServiceHub.Hotkey.UnregisterAll();
            var t = ParseHotkey(ServiceHub.Config.Get("hotkeys.toggle", "Ctrl+Alt+W"));
            var tr = ParseHotkey(ServiceHub.Config.Get("hotkeys.translate", "Ctrl+Alt+I"));
            var cl = ParseHotkey(ServiceHub.Config.Get("hotkeys.clipboard", "Ctrl+Alt+C"));
            ServiceHub.Hotkey.Register("toggle", t.Mod, t.Vk);
            ServiceHub.Hotkey.Register("translate", tr.Mod, tr.Vk);
            ServiceHub.Hotkey.Register("clipboard", cl.Mod, cl.Vk);
        }
        catch (Exception ex) { App.LogCrash("reload-hotkeys", ex); }
    }

    private void OnHotkey(object? sender, string name)
    {
        Dispatcher.Invoke(() =>
        {
            switch (name)
            {
                case "toggle":
                    ToggleIsland();
                    break;
                case "translate":
                    var text = ServiceHub.Clipboard.GetText() ?? "";
                    text = text.Trim();
                    if (text.Length == 0)
                    {
                        ShowActivity("剪贴板里没有文字");
                        return;
                    }
                    ShowPage("trans");
                    Expand();
                    var tp = (TranslatePage)_pages["trans"];
                    tp.TranslateClipboardText(text);
                    break;
                case "clipboard":
                    ShowPage("clip");
                    Expand();
                    ((ClipboardPage)_pages["clip"]).Refresh();
                    break;
            }
        });
    }

    private void ShowActivity(string title, string sub = "", double seconds = 2.4)
    {
        _compact.ShowActivity(new Activity
        {
            Type = "info",
            Glyph = Glyph.Bell,
            Title = title,
            Sub = sub,
        }, seconds);
        _chrome.RetargetCompact();
    }

    private void Media_Changed(object? sender, MediaState st)
    {
        Dispatcher.Invoke(() =>
        {
            var songChanged = _compact.Media.SongId != st.SongId;
            _compact.Media = st;
            _chrome.RetargetCompact();
            _compact.InvalidateVisual();
            if (songChanged) FetchLyrics(st);
        });
    }

    private void Weather_Updated(object? sender, WeatherState st)
    {
        Dispatcher.Invoke(() =>
        {
            _compact.Weather = st;
            _chrome.RetargetCompact();
            _compact.InvalidateVisual();
        });
    }

    private async void FetchLyrics(MediaState st)
    {
        try
        {
            if (ServiceHub.Lyrics is null || st.Title.Length == 0) return;
            var lyr = await ServiceHub.Lyrics.GetAsync(st);
            Dispatcher.Invoke(() =>
            {
                _compact.SetLyrics(lyr);
                _chrome.RetargetCompact();
            });
        }
        catch { }
    }

    private static (uint Mod, uint Vk) ParseHotkey(string s)
    {
        var parts = s.Split('+').Select(p => p.Trim().ToLowerInvariant()).ToArray();
        uint mod = 0, vk = 0;
        foreach (var p in parts)
        {
            switch (p)
            {
                case "ctrl": case "control": mod |= 0x0002; break;
                case "alt": mod |= 0x0001; break;
                case "shift": mod |= 0x0004; break;
                case "win": mod |= 0x0008; break;
                case "space": vk = 0x20; break;
                default:
                    if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = (uint)char.ToUpperInvariant(p[0]);
                    else if (p.StartsWith('f') && int.TryParse(p[1..], out var n)) vk = (uint)(0x70 + n - 1);
                    break;
            }
        }
        return (mod, vk);
    }

    // ---------------- 毛玻璃 ----------------
    private static void Try(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { App.LogCrash($"MainWindow init [{name}]", ex); }
    }
}