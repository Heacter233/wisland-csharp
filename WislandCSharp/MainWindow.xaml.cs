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
    private Border? _host;
    private bool _dragging;    private DateTime _lastFrame = DateTime.UtcNow;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoRedraw = 0x0008;

    private int _glassPx = int.MinValue, _glassPy, _glassPw, _glassPh;
    private string _blurRgnKey = "";
    private int _rgnW, _rgnH, _rgnR;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    private DateTime _lastBackdropRequest = DateTime.MinValue;
    private DateTime _lastGlassRefresh = DateTime.MinValue;
    private bool _renderingHooked;

    public MainWindow()
    {
        InitializeComponent();
        BuildUi();
        Loaded += (_, _) =>
        {
            StartTicker();

        // 预热：首次展开时页面还没量过，会在展开那一帧触发完整布局（实测 ~50ms 尖峰）。
        // 这里在启动后的空闲时机把所有页面量一遍，展开就只剩动画本身。
        Dispatcher.BeginInvoke(new Action(PrewarmPages),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _clockTimer.Start();
            // 窗口真正显示后再尝试一次：分层窗口在句柄刚创建时排除可能失败
            _backdrop?.RetryExclude();
        };
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
        _host = host;

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
            // 新页面的 Height 还是上一次 arrange 的旧值（甚至 0），直接取会拿到旧高度，
            // 于是切到更高的页面（如剪贴板）时底部被裁掉。这里先强制量一次再取高度。
            _pageHost.InvalidateMeasure();
            _pageHost.UpdateLayout();
            var page = _pages[key];
            page.Measure(new Size(636, 2000));
            var h = page.DesiredSize.Height;
            if (h < 10) h = CurrentPageHeight();
            _pageHeights[key] = h;
            _chrome.RetargetExpandedHeight(h + 66);
            _chrome.InvalidateMeasure();
            if (_chrome.SystemGlass)
            {
                // 高度变了：先把窗口放到新包络（动画结束后会贴回药丸），
                // 否则更高的页面底部会被当前窗口矩形裁掉。
                BeginGlassAnim();
            }
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

        // 展开态拖拽：只有点在岛体本身（空白处，不是页面内容/卡片/控件）才进入拖拽，
        // 避免抢掉剪贴板卡片点击、设置项点击等正常交互。
        if (ReferenceEquals(e.OriginalSource, _chrome))
        {
            e.Handled = true;
            CollapseToCursor();
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>命中的是不是可交互控件（按钮、输入框、列表等）。用于区分"拖岛"与"点控件"。</summary>
    private static bool IsInteractiveSource(object? src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase
                || d is System.Windows.Controls.Primitives.TextBoxBase
                || d is System.Windows.Controls.Primitives.Selector
                || d is System.Windows.Controls.Primitives.ScrollBar
                || d is Wisland.Controls.ScrollHost
                || d is System.Windows.Controls.ScrollViewer
                || d is System.Windows.Controls.Primitives.Thumb)
                return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>把岛收起到当前指针位置，然后立刻进入拖动（松手后按最近边吸附）。</summary>
    private void CollapseToCursor()
    {
        var pos = Mouse.GetPosition(this);
        var screenX = Left + pos.X;
        var screenY = Top + pos.Y;

        var wa = WorkAreaDip();
        // 选最近的一条边
        var gaps = new Dictionary<string, double>
        {
            ["top"] = Math.Abs(screenY - wa.Top),
            ["bottom"] = Math.Abs(wa.Bottom - screenY),
            ["left"] = Math.Abs(screenX - wa.Left),
            ["right"] = Math.Abs(wa.Right - screenX),
        };
        _edge = gaps.OrderBy(kv => kv.Value).First().Key;
        _chrome.AnchorEdge = _edge;
        _pos = _edge is "top" or "bottom"
            ? Math.Clamp((screenX - wa.Left) / wa.Width, 0, 1)
            : Math.Clamp((screenY - wa.Top) / wa.Height, 0, 1);

        Collapse();
        // 等收起动画走一段（窗口已经变小）再拖，避免拖动中尺寸还在变
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            BeginDrag();
        };
        t.Start();
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
        ServiceHub.RequestGlass = () => Try("glass-mode", ApplyGlassMode);
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

        // 系统模糊玻璃（默认开启）。--legacy-glass 可回退到抓屏方案做对比。
        Try("glass-mode", ApplyGlassMode);
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

        // DWM 模式下窗口每帧贴着药丸改尺寸，指针可能暂时落到"新窗口矩形之外"，
        // WPF 会误报 MouseLeave → 收起 → 又因为指针回到窗口上而展开，来回抖。
        // 只要指针还在本体矩形内就当作没离开。
        if (_chrome.BodyRect.Contains(Mouse.GetPosition(_chrome))) return;
        // 动画进行中也不收
        if (_glassAnimating) return;
        if (ServiceHub.Config.Get("island.pinned", false)) return;
        // 菜单/对话框打开期间不收
        if (_uiLocks.Count > 0) return;
        // 正在输入（输入框有键盘焦点）不收
        if (IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    /// <summary>把所有页面按展开尺寸量一遍（只测量不显示），消除首次展开的布局尖峰。</summary>
    private void PrewarmPages()
    {
        try
        {
            var w = 636.0;
            foreach (var page in _pages.Values)
            {
                page.Measure(new Size(w, 2000));
                var h = Math.Min(1200, Math.Max(120, page.DesiredSize.Height));
                page.Arrange(new Rect(0, 0, w, h));
            }
            // 展开高度也提前算一次
            _chrome.ExpandedHeight = () => CurrentPageHeight() + 66;
            _ = CurrentPageHeight();
        }
        catch (Exception ex) { App.LogCrash("prewarm pages", ex); }
    }

    private void StartTicker()
    {
        _glassResume.Tick += (_, _) =>
        {
            _glassResume.Stop();
            if (_chrome.SystemGlass) ApplyAccent(_glassBlurBehind);
        };
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

        // 系统模糊模式下窗口必须贴合胶囊：accent 模糊是窗口矩形级的。
        // 但逐帧 SetWindowPos 会让 DWM 每帧重新合成亚克力 → 展开/收起卡成幻灯片。
        // 因此：动画期间关掉 accent、把窗口一次性设成"目标尺寸包络"，动画结束再贴回药丸。
        if (_chrome.SystemGlass && !(moving || navMoving)) EndGlassAnim();

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

        // 背景模糊：仅在抓屏回退模式（--legacy-glass）下需要，系统模糊模式下完全不抓屏。
        if (_backdrop != null && !_dragging && !_chrome.SystemGlass &&
            (now - _lastBackdropRequest).TotalMilliseconds > 220)
        {
            _lastBackdropRequest = now;
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

        // 在动画第一帧之前完成窗口 resize + 关 DWM 合成。
        // 这步在分层窗口上约 15ms，落在动画之外（此刻药丸还没开始动），不会表现为动画掉帧。
        BeginGlassAnim();

        _pageHost.InvalidateMeasure();
        _pageHost.UpdateLayout();

        Dispatcher.BeginInvoke(new Action(() => _pageHost.UpdateLayout()),
            System.Windows.Threading.DispatcherPriority.Loaded);
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
        BeginGlassAnim();
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
        _dragging = true;
        try { DragMove(); }
        catch { }
        finally { _dragging = false; }
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
            if (_chrome.SystemGlass) SyncGlassWindowGeometry();
            else PositionWindow();
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
        // 窗口位置变了 → 背后的桌面也变了，请求重新抓一次背景
        _backdrop?.Invalidate();
    }
    /// <summary>兼容旧调用名。</summary>
    private void ApplyAnchor()
    {
        if (_chrome.SystemGlass) SyncGlassWindowGeometry();
        else PositionWindow();
    }

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

    // ---------------- 毛玻璃调试开关（--glass-debug） ----------------

    /// <summary>
    /// 系统模糊模式下让窗口贴合胶囊（窗口 = 胶囊 + 边距）。
    ///
    /// 原因：accent 模糊是窗口矩形级的，而且 SetWindowRgn 对分层窗口无效（实测 getKind=0），
    /// 只能靠"窗口本身跟着胶囊变"让模糊跟随形状。
    /// 窗口保持分层（AllowsTransparency），因此抗锯齿圆角与外阴影都不受影响。
    /// </summary>
    private void SyncGlassWindowGeometry() => SyncGlassWindow(_chrome.BodySize);

    private bool _glassAnimating;

    private readonly System.Windows.Threading.DispatcherTimer _glassResume =
        new() { Interval = TimeSpan.FromMilliseconds(90) };

    /// <summary>当前 accent 变体（true = 经典 blur-behind 真模糊；false = 亚克力）。</summary>
    private bool _glassBlurBehind = true;

    /// <summary>玻璃模式：system（DWM 动态模糊）/ capture（抓屏静态模糊）/ solid（实色无模糊）。</summary>
    private string _glassMode = "system";

    /// <summary>按 config 应用玻璃模式（启动与配置变更时调用）。</summary>
    private void ApplyGlassMode()
    {
        var cfg = ServiceHub.Config.Get("island.glass", "system");
        if (Array.Exists(Environment.GetCommandLineArgs(),
                a => string.Equals(a, "--capture-glass", StringComparison.OrdinalIgnoreCase)))
            cfg = "capture";
        _glassMode = cfg;

        var solid = string.Equals(cfg, "solid", StringComparison.OrdinalIgnoreCase);
        var capture = string.Equals(cfg, "capture", StringComparison.OrdinalIgnoreCase);
        var system = !solid && !capture;

        _chrome.SolidGlass = solid;
        _chrome.SystemGlass = system;
        _chrome.FillWindow = system;
        // 实色/抓屏模式不需要角部雾化；DWM 模式留可调旋钮
        _chrome.CornerFog = system && Environment.GetEnvironmentVariable("WISLAND_NO_FOG") != "1";
        _chrome.CornerFogAlpha = (byte)Math.Clamp(
            ServiceHub.Config.Get("island.glass_fog", 0), 0, 255);
        // DWM 模式改成小圆角矩形：accent 模糊矩形与本体形状的差集只剩很小的角
        _chrome.CornerRadiusOverride = system
            ? Math.Clamp(ServiceHub.Config.Get("island.glass_corner", 14), 0, 60)
            : 0;
        _chrome.HaloWidth = 0;
        Pages.SettingsPage.SolidScrollPlate = !system;

        if (system)
        {
            var variant = Environment.GetEnvironmentVariable("WISLAND_GLASS_VARIANT")
                          ?? ServiceHub.Config.Get("island.glass_accent", "blur");
            ApplyAccent(!string.Equals(variant, "acrylic", StringComparison.OrdinalIgnoreCase));
            SyncGlassWindowGeometry();
        }
        else
        {
            SystemBlur.Apply(_hwnd, false);
        }

        _chrome.InvalidateVisual();
    }

    private void ApplyAccent(bool blurBehind)
    {
        _glassBlurBehind = blurBehind;
        SystemBlur.Apply(_hwnd, true, acrylic: !blurBehind);
    }

    /// <summary>
    /// 动画开始前调用：关掉 DWM 合成、把窗口一次性放大成"包络"，
    /// 之后整段动画期间窗口尺寸和 accent 都不再变动 → 动画只受 WPF 绘制影响，不卡。
    /// </summary>
    private void BeginGlassAnim()
    {
        if (!_chrome.SystemGlass || _glassAnimating) return;
        _glassAnimating = true;
        _chrome.CenterBody = true;
        SystemBlur.Apply(_hwnd, false);

        var (cw, ch) = _chrome.BodySize;
        var (tw, th) = _chrome.BodyTarget;
        SyncGlassWindow((Math.Max(cw, tw) + 2, Math.Max(ch, th) + 2));
    }

    /// <summary>动画结束后调用：贴回药丸尺寸，稍后再把 DWM 合成开回来（错开最后一帧）。</summary>
    private void EndGlassAnim()
    {
        if (!_glassAnimating) return;
        _glassAnimating = false;
        _chrome.CenterBody = false;
        SyncGlassWindowGeometry();

        _glassResume.Stop();
        _glassResume.Start();
    }

    /// <summary>把窗口贴到给定本体尺寸上（系统模糊模式用，窗口矩形=模糊矩形）。</summary>
    private void SyncGlassWindow((double W, double H) body)
    {
        if (_hwnd == IntPtr.Zero) return;

        var bw = body.W;
        var bh = body.H;
        var m = IslandChrome.GlassRegionMargin;

        // 整数量化：亚像素级的宽/位变化会让窗口左右来回抖 1px（60fps 下就是"疯狂跳动"）
        var winW = (int)Math.Round(bw + 2 * m);
        var winH = (int)Math.Round(bh + 2 * m);
        var bodyW = (int)Math.Round(bw);

        var wa = WorkAreaDip();
        double bodyLeft, bodyTop;
        switch (_edge)
        {
            case "bottom":
                bodyLeft = wa.Left + wa.Width * _pos - bodyW / 2.0;
                bodyTop = wa.Bottom - bh;
                break;
            case "left":
                bodyLeft = wa.Left;
                bodyTop = wa.Top + wa.Height * _pos - bh / 2;
                break;
            case "right":
                bodyLeft = wa.Right - bodyW;
                bodyTop = wa.Top + wa.Height * _pos - bh / 2;
                break;
            default:
                bodyLeft = wa.Left + wa.Width * _pos - bodyW / 2.0;
                bodyTop = wa.Top;
                break;
        }

        var left = (int)Math.Round(bodyLeft - m);
        var top = (int)Math.Round(bodyTop - m);

        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var px = (int)Math.Round(left * sx);
        var py = (int)Math.Round(top * sy);
        var pw = (int)Math.Round(winW * sx);
        var ph = (int)Math.Round(winH * sy);

        // 只在整数结果变化时才动窗口
        if (px == _glassPx && py == _glassPy && pw == _glassPw && ph == _glassPh) return;
        _glassPx = px; _glassPy = py; _glassPw = pw; _glassPh = ph;

        // 位置与尺寸一次性设置，避免"先改宽再改左"的中间帧导致左右抖动
        SetWindowPos(_hwnd, IntPtr.Zero, px, py, pw, ph,
            SwpNoZorder | SwpNoActivate | SwpNoRedraw);

        // 实验：DWM 区域限制（DWM_BB_BLURREGION）能否裁掉窗口矩形级的 accent 模糊
    }

    /// <summary>
    /// 把窗口区域切成"圆角矩形"（= 药丸外扩 8px）。
    ///
    /// 目的：accent 模糊铺满整个窗口矩形，若窗口是方角，
    /// 药丸四个角外侧也会被模糊 → 看起来像有个矩形底板。
    /// 改用圆角窗口区域后，模糊也跟着变成圆角。
    /// </summary>
    private void ApplyRoundedWindowRegion(int pw, int ph)
    {
        if (_hwnd == IntPtr.Zero || pw < 2 || ph < 2) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Min(dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX,
                             dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY);
        var radius = (int)Math.Round((_chrome.BodyCornerRadius + IslandChrome.GlassRegionMargin) * scale);
        radius = Math.Clamp(radius, 1, Math.Min(pw, ph) / 2);

        if (pw == _rgnW && ph == _rgnH && radius == _rgnR) return;

        var rgn = CreateRoundRectRgn(0, 0, pw + 1, ph + 1, radius * 2, radius * 2);
        if (rgn == IntPtr.Zero) return;
        if (SetWindowRgn(_hwnd, rgn, true) == 0)
        {
            // 设置失败：区域句柄仍归我们，需自行释放
            DeleteObject(rgn);
            return;
        }
        _rgnW = pw; _rgnH = ph; _rgnR = radius;
    }

    /// <summary>
    /// 把 DWM 模糊限制在胶囊形状内（窗口坐标）。
    /// 不做的话，窗口四周边距同样会被模糊 → 药丸外出现一圈亮矩形。
    /// </summary>
    private void UpdateBlurRegion()
    {
        if (_hwnd == IntPtr.Zero) return;

        var body = _chrome.BodyRect;
        if (body.Width < 1 || body.Height < 1) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var x = (int)Math.Round(body.X * sx);
        var y = (int)Math.Round(body.Y * sy);
        var w = (int)Math.Round(body.Width * sx);
        var h = (int)Math.Round(body.Height * sy);
        var r = (int)Math.Round(_chrome.BodyCornerRadius * Math.Min(sx, sy));

        var key = $"{x},{y},{w},{h},{r}";
        if (key == _blurRgnKey) return;
        _blurRgnKey = key;

        var rgn = CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, r * 2, r * 2);
        SystemBlur.SetBlurRegion(_hwnd, rgn);
    }


    // ---------------- 毛玻璃 ----------------
    private static void Try(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { App.LogCrash($"MainWindow init [{name}]", ex); }
    }
}
