using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

    /// <summary>NOTIFYICONDATAW 里的 GUID 字段（16 字节；用两个 ulong 表示，blittable 无需封送）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GUID16
    {
        public ulong A;
        public ulong B;
    }

    /// <summary>
    /// Shell_NotifyIcon 的 NOTIFYICONDATAW。
    /// 注意字段顺序/宽度必须与 Windows SDK 完全一致：早期版本把 dwStateMask 写成 IntPtr
    /// （x64 下多 4 字节）、szInfoTitle 写成 128，导致后续字段整体错位——只用 V1 字段时"碰巧能用"，
    /// 一旦用气泡/状态标记就会写坏内存。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;      // 与 uVersion 共用（联合体）
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public GUID16 guidItem;
        public IntPtr hBalloonIcon;
    }

    private const int NimAdd = 0;
    private const int NimDelete = 2;

    /// <summary>托盘图标句柄（退出时要 DestroyIcon，否则泄漏 GDI 句柄）。</summary>
    private IntPtr _trayIcon = IntPtr.Zero;

    /// <summary>托盘图标是否已成功添加（只有添加过才需要删除）。</summary>
    private bool _trayAdded;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
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
    private Border _pluginBar = null!;
    private StackPanel _pluginRow = null!;
    private Plugins.PluginWatcher? _pluginWatcher;
    private bool _pluginReloading;

    /// <summary>插件页：key → (视图, 内容高度, 图标按钮)。</summary>
    private readonly Dictionary<string, (FrameworkElement View, double Height, Button Btn)> _pluginPages = new();
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
    private bool _dragging;
    private DateTime _lastFrame = DateTime.UtcNow;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoRedraw = 0x0008;

    private int _glassPx = int.MinValue, _glassPy, _glassPw, _glassPh;

    // 说明：曾经用 SetWindowRgn 给窗口做圆角区域（ApplyRoundedWindowRegion）
    // 和把 DWM 模糊限制在胶囊内（UpdateBlurRegion），实测对 AllowsTransparency 的
    // 分层窗口不生效（getKind=0），两条路径都是死代码，已连同 P/Invoke 一起删除。
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
        Try("filedrop", SetupFileDrop);

        // 收起态新消息提示：Inbox 变化 → 绿点 + 滚动/弹出展示
        Try("notify-watch", () =>
        {
            ServiceHub.Inbox.Changed += OnInboxNotifyChanged;
            _notifyTimer.Tick += (_, _) => AdvanceNotify();
            _notifySweep.Tick += (_, _) => SweepPopupLines();
            _chrome.NotifyTickerMode = NotifyTickerStyle;
            _chrome.SetUnread(ServiceHub.Inbox.UnreadCount());
            // 启动时可能已有未读（例如 --inbox-demo 提前推的），补一次
            OnInboxNotifyChanged(null, EventArgs.Empty);
        });

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

        // 插件页图标行：默认隐藏（无插件时高度 0，不影响其他元素位置）
        _pluginRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pluginScroll = new ScrollViewer
        {
            Content = _pluginRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 560,
        };
        pluginScroll.PreviewMouseWheel += (_, e) =>
        {
            pluginScroll.ScrollToHorizontalOffset(pluginScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        _pluginBar = new Border
        {
            Child = pluginScroll,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });   // 主导航
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // 插件页图标行（无插件时高度为 0）
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });    // 间隔
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_nav, 0);
        Grid.SetRow(_pluginBar, 1);
        Grid.SetRow(_pageHost, 3);
        inner.Children.Add(_nav);
        inner.Children.Add(_pluginBar);
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
        _chrome.ExpandedHeight = () => CurrentPageHeight() + ChromeExtra;
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
        // 高度稍后按"所有页面的最大展开高度"再算（FitWindowToPages）
    }

    /// <summary>展开态里除页面内容之外的高度：导航 36 + 间隔 8 + 上下内边距 22。</summary>
    private const double ChromeBaseExtra = 66;

    /// <summary>插件图标行高度（图标 26 + 上边距 4）。</summary>
    private const double PluginRowHeight = 30;

    /// <summary>
    /// 展开态额外高度。有插件页图标行时要把这一行算进去，
    /// 否则页面底部的启动栏 / 天气逐时区会被裁掉。
    /// </summary>
    private double ChromeExtra =>
        ChromeBaseExtra + (_pluginBar != null && _pluginBar.Visibility == Visibility.Visible
            ? PluginRowHeight : 0);

    /// <summary>当前页面要求的高度（页面自身 Height 优先）。</summary>
    private double CurrentPageHeight()
    {
        if (_pluginPages.TryGetValue(_currentPage, out var pp))
        {
            if (pp.View.Height > 10) return pp.View.Height;
            return pp.Height;
        }
        if (_pages.TryGetValue(_currentPage, out var page) && page.Height > 10) return page.Height;
        return _pageHeights.GetValueOrDefault(_currentPage, 300);
    }

    /// <summary>当前页面是否为插件页。</summary>
    private bool IsPluginPage(string key) => _pluginPages.ContainsKey(key);

    private void ShowPage(string key, bool force = false)
    {
        var isPlugin = IsPluginPage(key);
        if (!isPlugin && !_pages.ContainsKey(key)) return;
        if (!force && key == _currentPage) return;
        _currentPage = key;

        if (isPlugin)
        {
            var entry = _pluginPages[key];
            _pageHost.Content = entry.View;
            _nav.ClearSelection();          // 主选中的高亮让给插件页
            UpdatePluginSelection(key);
        }
        else
        {
            _pageHost.Content = _pages[key];
            _nav.SetCurrent(key);
            UpdatePluginSelection(null);
        }

        var content = _pageHost.Content as FrameworkElement;
        if (_chrome.IsExpanded)
        {
            // 新页面的 Height 还是上一次 arrange 的旧值（甚至 0），直接取会拿到旧高度，
            // 于是切到更高的页面（如剪贴板）时底部被裁掉。这里先强制量一次再取高度。
            _pageHost.InvalidateMeasure();
            _pageHost.UpdateLayout();
            content?.Measure(new Size(636, 2000));
            var h = content?.DesiredSize.Height ?? 0;
            if (h < 10) h = CurrentPageHeight();
            _pageHeights[key] = h;
            _chrome.RetargetExpandedHeight(h + ChromeExtra);
            _chrome.InvalidateMeasure();
            if (_chrome.SystemGlass)
            {
                // 高度变了：先把窗口放到新包络（动画结束后会贴回药丸），
                // 否则更高的页面底部会被当前窗口矩形裁掉。
                BeginGlassAnim();
            }
            if (!_chrome.SystemGlass) FitWindowToPages();

            // 再等一帧复测一次：部分页面（剪贴板/设置）内容高度要等首次 arrange 后才稳定
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _pageHost.UpdateLayout();
                    var pg = _pageHost.Content as FrameworkElement;
                    if (pg == null) return;
                    pg.Measure(new Size(636, 2000));
                    var h2 = pg.DesiredSize.Height;
                    if (h2 > 10 && Math.Abs(h2 - h) > 2)
                    {
                        _pageHeights[key] = h2;
                        _chrome.RetargetExpandedHeight(h2 + ChromeExtra);
                        _chrome.InvalidateMeasure();
                        if (_chrome.SystemGlass) BeginGlassAnim();
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        ServiceHub.Config.Set("island.last_tab", key, save: false);
    }

    /// <summary>加载全部插件并安装页面/设置分组（启动与热重载共用）。</summary>
    private void LoadPluginsAndInstall()
    {
        // 后台加载：插件 OnLoad 是第三方代码，不能占着 UI 线程等它
        _ = LoadPluginsAndInstallAsync();
    }

    private async Task LoadPluginsAndInstallAsync()
    {
        try
        {
            var results = await Plugins.PluginLoader.LoadAllAsync(
                AppPaths.BaseDir,
                ServiceHub.Config.DataDir,
                ServiceHub.Config,
                Dispatcher);

            // 回到 UI 线程安装页面/设置分组（视觉树只能在 UI 线程动）
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var r in results)
                {
                    if (r.State == Plugins.PluginState.Failed)
                        App.LogCrash($"plugin [{r.Manifest?.Id ?? System.IO.Path.GetFileName(r.Directory)}]",
                            new Exception(r.Error));
                }

                // 插件页：主导航下方单独一行（无页面时该行不占高度）
                InstallPluginPages(results);

                App.DebugNote($"plugins: {results.Count} 个目录，" +
                              $"成功 {results.Count(x => x.State == Plugins.PluginState.Loaded)}");
            });
        }
        catch (Exception ex)
        {
            App.LogCrash("plugins load", ex);
        }
    }

    /// <summary>
    /// 热重载：卸载全部插件后重新扫描加载，并同步界面。
    ///
    /// 因为加载走影子副本（data/plugins/.shadow），源目录的文件一直没被锁定，
    /// 所以用户可以直接覆盖 DLL 再触发重载。
    /// </summary>
    private void ReloadPlugins()
    {
        if (_pluginReloading) return;
        _pluginReloading = true;
        _ = ReloadPluginsAsync();
    }

    private async Task ReloadPluginsAsync()
    {
        var currentWasPlugin = IsPluginPage(_currentPage);
        var currentKey = _currentPage;
        try
        {
            App.DebugNote($"plugins: 热重载开始（{_pluginWatcher?.LastReason}）");

            var results = await Plugins.PluginLoader.ReloadAsync(
                AppPaths.BaseDir,
                ServiceHub.Config.DataDir,
                ServiceHub.Config,
                Dispatcher);

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var r in results)
                {
                    if (r.State == Plugins.PluginState.Failed)
                        App.LogCrash($"plugin reload [{r.Manifest?.Id ?? System.IO.Path.GetFileName(r.Directory)}]",
                            new Exception(r.Error));
                }

                InstallPluginPages(results);

                // 当前页是被卸载/改名的插件页 → 退回主页，避免停在空白页
                if (currentWasPlugin && !_pluginPages.ContainsKey(currentKey))
                {
                    ShowPage("home", force: true);
                }
                else if (_pluginPages.ContainsKey(currentKey))
                {
                    // 仍在，但视图是新构建的实例，需要重新挂到页面上
                    ShowPage(currentKey, force: true);
                }

                // 设置页在显示中则重建（插件分组、状态、权限都会变）
                if (_pages.TryGetValue("set", out var sp) && sp is Pages.SettingsPage setPage)
                    setPage.Rebuild();

                App.DebugNote($"plugins: 热重载完成，成功 {results.Count(x => x.State == Plugins.PluginState.Loaded)}");
            });
        }
        catch (Exception ex)
        {
            App.LogCrash("plugin reload", ex);
        }
        finally
        {
            _pluginReloading = false;
        }
    }

    /// <summary>
    /// 只重载一个插件（其它插件继续运行）。
    /// 目录被删时该插件从列表消失，当前页若属于它会退回主页。
    /// </summary>
    private void ReloadPlugin(string id)
    {
        if (_pluginReloading) return;
        _pluginReloading = true;
        _ = ReloadPluginAsync(id);
    }

    private async Task ReloadPluginAsync(string id)
    {
        var currentKey = _currentPage;
        var currentWasPlugin = IsPluginPage(currentKey);
        try
        {
            App.DebugNote($"plugins: 单独重载 {id}");

            var result = await Plugins.PluginLoader.ReloadOneAsync(
                id, AppPaths.BaseDir, ServiceHub.Config.DataDir, ServiceHub.Config, Dispatcher);

            await Dispatcher.InvokeAsync(() =>
            {
                if (result != null && result.State == Plugins.PluginState.Failed)
                    App.LogCrash($"plugin reload [{id}]", new Exception(result.Error));

                // 页面行按最新的加载结果整体重建（新增/移除/改名都能反映）
                InstallPluginPages(Plugins.PluginLoader.Loaded);

                if (currentWasPlugin && !_pluginPages.ContainsKey(currentKey))
                    ShowPage("home", force: true);
                else if (_pluginPages.ContainsKey(currentKey))
                    ShowPage(currentKey, force: true);

                if (_pages.TryGetValue("set", out var sp) && sp is Pages.SettingsPage setPage)
                    setPage.Rebuild();

                App.DebugNote($"plugins: 单独重载完成 {id}（{result?.State.ToString() ?? "已移除"}）");
            });
        }
        catch (Exception ex)
        {
            App.LogCrash($"plugin reload [{id}]", ex);
        }
        finally
        {
            _pluginReloading = false;
        }
    }

    /// <summary>
    /// 安装插件页面：在主导航下方生成一行图标按钮。
    /// 没有插件页面时整行保持 Collapsed（高度 0），不影响其他元素位置。
    /// </summary>
    private void InstallPluginPages(IReadOnlyList<Plugins.PluginLoadResult> results)
    {
        _pluginPages.Clear();
        _pluginRow.Children.Clear();

        foreach (var r in results)
        {
            if (r.State != Plugins.PluginState.Loaded) continue;

            foreach (var page in r.Pages)
            {
                var key = page.Key;
                if (_pluginPages.ContainsKey(key) || _pages.ContainsKey(key))
                {
                    App.DebugNote($"plugin page key 冲突，已跳过：{key}");
                    continue;
                }

                // 视图延迟构建：首次点开才 Build，避免启动时把 N 个插件页面全部造出来
                FrameworkElement? view = null;
                FrameworkElement GetView()
                {
                    if (view != null) return view;
                    try
                    {
                        view = page.Build() ?? new StackPanel();
                    }
                    catch (Exception ex)
                    {
                        App.LogCrash($"plugin page [{key}]", ex);
                        view = new TextBlock
                        {
                            Text = $"插件页面构建失败：{ex.Message}",
                            Margin = new Thickness(16),
                            TextWrapping = TextWrapping.Wrap,
                        };
                    }
                    return view;
                }

                var btn = BuildPluginPageButton(page, key, GetView);
                _pluginRow.Children.Add(btn);
                _pluginPages[key] = (new LazyPageHost(GetView, page.Height), page.Height, btn);
            }
        }

        _pluginBar.Visibility = _pluginPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 插件图标行一旦出现，展开态要多占这一行的高度；
        // 若此刻岛已展开（比如开机后立即展开），需要立刻重算，否则页面底部被裁。
        if (_chrome != null && _chrome.IsExpanded)
            _chrome.RetargetExpandedHeight(CurrentPageHeight() + ChromeExtra);

        App.DebugNote($"plugin pages: {_pluginPages.Count}");
    }

    /// <summary>插件页图标按钮（与主导航同一套观感）。</summary>
    private Button BuildPluginPageButton(Plugins.PluginPageDescriptor page, string key, Func<FrameworkElement> getView)
    {
        var glyph = string.IsNullOrWhiteSpace(page.Glyph) ? Glyph.Gear : page.Glyph;

        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Icon),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "AppText");

        var host = new Border
        {
            Width = 30,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = Brushes.Transparent,
            Child = text,
        };

        var btn = new Button
        {
            Content = host,
            Width = 36,
            Margin = new Thickness(2, 0, 2, 0),
            ToolTip = string.IsNullOrWhiteSpace(page.Title) ? key : $"{page.Title}（插件页面）",
            Style = null,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
            Template = BuildFlatButtonTemplate(),
        };
        btn.SetResourceReference(Control.ForegroundProperty, "AppText");
        btn.Click += (_, _) =>
        {
            _ = getView();       // 首次点开时构建
            ShowPage(key);
        };
        return btn;
    }

    private static ControlTemplate BuildFlatButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var t = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ButtonBase)) { VisualTree = border };
        return t;
    }

    /// <summary>高亮当前插件页按钮（null = 取消高亮）。</summary>
    private void UpdatePluginSelection(string? key)
    {
        foreach (var kv in _pluginPages)
        {
            var selected = string.Equals(kv.Key, key, StringComparison.Ordinal);
            kv.Value.Btn.Background = selected
                ? new SolidColorBrush(Themes.ActivePill())
                : Brushes.Transparent;
            if (kv.Value.Btn.Content is Border b && b.Child is TextBlock tb)
            {
                tb.Foreground = new SolidColorBrush(selected ? Themes.PillText() : Themes.Text(0.75));
                tb.InvalidateVisual();
            }
        }
    }

    /// <summary>插件页视图代理：延迟构建，未构建前高度取声明值。</summary>
    private sealed class LazyPageHost : FrameworkElement
    {
        private readonly Func<FrameworkElement> _factory;
        private FrameworkElement? _child;

        public LazyPageHost(Func<FrameworkElement> factory, double height)
        {
            _factory = factory;
            Height = height;
        }

        /// <summary>
        /// 真实内容。首次访问时构建并**注册进可视树** ——
        /// 不调用 AddVisualChild 的话，子树的失效不会传播到渲染根，
        /// 表现为"插件改了界面但不刷新"。
        /// </summary>
        public FrameworkElement Real
        {
            get
            {
                if (_child == null)
                {
                    _child = _factory();
                    AddVisualChild(_child);
                    AddLogicalChild(_child);
                }
                return _child;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var child = Real;
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            return new Size(availableSize.Width, double.IsInfinity(availableSize.Height)
                ? child.DesiredSize.Height : availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Real.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }

        protected override int VisualChildrenCount => _child != null ? 1 : 0;

        protected override Visual GetVisualChild(int index) => Real;
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

        // 展开态拖拽：点在"岛体自定义区域"（导航栏与页面之外的空白/本体）才算拖拽。
        // 但不要一按下就收起（普通单击也会命中空白区），改成"按住 0.5 秒"才判定为拖拽。
        if (IsChromeBlankArea(e.OriginalSource))
        {
            e.Handled = true;
            _holdPending = true;
            _dragHold.Stop();
            _dragHold.Start();
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>
    /// 命中点是否属于"岛体空白区"（可以拖动）：从原始命中元素向上找，
    /// 路径中若出现页面宿主(_pageHost)/导航栏(_nav)/可交互控件，则不是。
    /// </summary>
    private bool IsChromeBlankArea(object? src)
    {
        if (IsInteractiveSource(src)) return false;
        var d = src as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _pageHost) || ReferenceEquals(d, _nav)) return false;
            if (ReferenceEquals(d, _chrome)) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        // 命中的是本窗口之外的子树（极少见）：交给默认处理
        return ReferenceEquals(src, _chrome);
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

    private readonly System.Windows.Threading.DispatcherTimer _dragHold =
        new() { Interval = TimeSpan.FromMilliseconds(300) };

    private bool _holdPending;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        // 提前松手 = 单击，取消拖拽判定
        if (_holdPending)
        {
            _holdPending = false;
            _dragHold.Stop();
        }
        base.OnMouseLeftButtonUp(e);
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
            // 收起态字幕停留时间：0 = 一直显示到停止聆听
            void SyncSubHold() => _compact.SubHold = ServiceHub.Config.Get("live.collapsed_hold_s", 0.0);
            SyncSubHold();

            ServiceHub.Live.RunningChanged += (_, on) => Dispatcher.Invoke(() =>
            {
                SyncSubHold();
                _compact.SetSubRunning(on);
            });
            ServiceHub.Live.Partial += (_, jp) => Dispatcher.Invoke(() =>
            {
                _compact.SetPartial(jp);
                _chrome.RetargetCompact();
            });
            ServiceHub.Live.Segment += (_, s) => Dispatcher.Invoke(() =>
            {
                SyncSubHold();
                _compact.PushSegment(s.sid, s.jp);
                _chrome.RetargetCompact();
            });
            ServiceHub.Live.LiveZh += (_, s) => Dispatcher.Invoke(() =>
            {
                SyncSubHold();
                _compact.SetSegmentZh(s.sid, s.zh);
                _chrome.RetargetCompact();
            });
            ServiceHub.Live.FinalZh += (_, s) => Dispatcher.Invoke(() => _compact.SetSegmentZh(s.sid, s.zh));
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
                if (_chrome.IsExpanded) _chrome.RetargetExpandedHeight(CurrentPageHeight() + ChromeExtra);
                else _chrome.RetargetCompact();
            };

            // 插件改了页面视觉树后要主动重绘（宿主空闲时不会自己刷新）
            ServiceHub.RequestRepaint = () =>
            {
                _pageHost.InvalidateVisual();
                ServiceHub.RequestRetargetHeight?.Invoke();
                _chrome.InvalidateVisual();
            };

            // 设置页按钮：重载全部 / 重载单个插件
            ServiceHub.RequestReloadPlugins = id =>
            {
                if (string.IsNullOrWhiteSpace(id)) ReloadPlugins();
                else ReloadPlugin(id);
            };
        });

        // 插件加载：扫描 plugins/ 目录（在 hooks 之后，保证插件能拿到 RequestExpand 等回调）
        Try("plugins", () =>
        {
            if (!ServiceHub.Config.Get("plugins.enabled", true))
            {
                App.DebugNote("plugins: 已禁用（plugins.enabled=false）");
                return;
            }

            LoadPluginsAndInstall();

            // 热重载：plugins/ 下的 dll / plugin.json 变化后自动重载
            if (ServiceHub.Config.Get("plugins.hot_reload", true))
            {
                Try("plugin-watcher", () =>
                {
                    _pluginWatcher = new Plugins.PluginWatcher(
                        Plugins.PluginLoader.PluginsRoot(AppPaths.BaseDir),
                        () => ReloadPlugins(),
                        Dispatcher);
                    App.DebugNote("plugins: 热重载已启用（监视 plugins/）");
                });
            }
        });

        // 调试：--quit-after=毫秒 让程序自动退出（用于测量退出路径耗时/回归验证）
        Try("quit-after", () =>
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--quit-after=", StringComparison.OrdinalIgnoreCase));
            if (arg == null) return;
            if (!int.TryParse(arg["--quit-after=".Length..], out var ms) || ms < 500) return;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                App.DebugNote($"quit-after: 触发退出（{ms}ms）");
                Application.Current.Shutdown();
            };
            timer.Start();
            App.DebugNote($"quit-after: {ms}ms 后自动退出");
        });

        Try("debugpage", () =>
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--page:", StringComparison.OrdinalIgnoreCase));
            if (arg != null)
            {
                var key = arg["--page:".Length..];

                // 模型管理现在是翻译页的子页：--page:model 仍然可用，映射到「翻译 → 模型」
                if (key.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    ShowPage("trans");
                    ((TranslatePage)_pages["trans"]).ShowModels();
                    Expand();
                    return;
                }

                if (_pages.ContainsKey(key) || IsPluginPage(key))
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

        // 调试：--launcher-test=<path> 走一遍"拖入启动栏"的添加与落盘逻辑
        Try("launcher-test", () =>
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--launcher-test=", StringComparison.OrdinalIgnoreCase)) continue;
                var target = arg["--launcher-test=".Length..];
                ShowPage("home");
                if (_pages.TryGetValue("home", out var pg) && pg is HomePage hp)
                {
                    hp.AddDroppedPaths(new[] { target });
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(Services.AppPaths.DataDir, "launcher-test.log"),
                        $"{DateTime.Now:HH:mm:ss} added '{target}'{Environment.NewLine}");
                }
            }
        });

        // 调试：--open-test=qq|wechat|mail 验证"磁贴点击打开程序"的兜底链路
        Try("open-test", () =>
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--open-test=", StringComparison.OrdinalIgnoreCase)) continue;
                var kind = arg["--open-test=".Length..];
                var configured = ServiceHub.Config.Get($"notify.open.{kind}", "");
                // 后台执行并记录结果（这条链路会轮询等窗口，不能占用 UI 线程）
                _ = Services.AppLauncher.LaunchAsync(kind, configured).ContinueWith(t =>
                {
                    var ok = t.Status == TaskStatus.RanToCompletion && t.Result;
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(Services.AppPaths.DataDir, "app-launch.log"),
                            $"{DateTime.Now:HH:mm:ss.fff} [open-test] result={ok}{Environment.NewLine}");
                    }
                    catch { }
                });
            }
        });

        // 调试：--inbox-demo 推两条示例消息，用来验证消息页的插件推送通道
        Try("inbox-demo", () =>
        {
            if (!Array.Exists(Environment.GetCommandLineArgs(),
                    a => string.Equals(a, "--inbox-demo", StringComparison.OrdinalIgnoreCase))) return;
            ServiceHub.Inbox.Push(new Services.PluginMessage
            {
                Source = "clash",
                SourceName = "Clash",
                Title = "规则已更新",
                Body = "订阅已同步，共 1284 条规则",
                Level = "info",
                IconGlyph = "\uE774",   // Globe
                IconColor = "#E8544E",
            });
            ServiceHub.Inbox.Push(new Services.PluginMessage
            {
                Source = "bilibili",
                SourceName = "哔哩哔哩",
                Title = "新番更新",
                Body = "《葬送的芙莉莲》第 12 话",
                Level = "warn",
                IconGlyph = "\uE714",   // Video
                IconColor = "#FB7299",
                Uri = "https://www.bilibili.com",
            });
            // 演示"系统通知 → Inbox"：Emit 两条系统通知，验证镜像与磁贴
            ServiceHub.Notify.Emit(new Services.NotifyItem
            {
                Kind = "qq", App = "QQ", Name = "QQ",
                Title = "QQ 群消息", Body = "[演示] 系统通知已镜像到消息页",
                Time = DateTime.Now,
            });
            ServiceHub.Notify.Emit(new Services.NotifyItem
            {
                Kind = "wechat", App = "WeChat", Name = "微信",
                Title = "微信消息", Body = "[演示] 系统通知已镜像到消息页",
                Time = DateTime.Now,
            });

            // 演示"同时多条"与"图片通知"
            for (var i = 1; i <= 3; i++)
            {
                ServiceHub.Inbox.Push(new Services.PluginMessage
                {
                    Source = "burst",
                    SourceName = "批量通知",
                    Title = $"第 {i} 条",
                    Body = $"同时到达的消息 {i}",
                    Level = "info",
                    IconGlyph = "\uE8BD",
                    IconColor = "#7B61FF",
                });
            }
            // 再塞 7 个来源，用来验证"磁贴两行 + 滚轮查找 + 右键置顶"
            var demo = new (string Src, string Name, string Glyph, string Color)[]
            {
                ("weather", "天气预警", "\uE9CA", "#4A90D9"),
                ("calendar", "日程提醒", "\uE787", "#7B61FF"),
                ("download", "下载完成", "\uE896", "#31A36B"),
                ("stock", "股价提醒", "\uE9D9", "#E8892B"),
                ("git", "代码推送", "\uE8A5", "#6B7280"),
                ("rss", "RSS 更新", "\uE8F1", "#C2410C"),
                ("anki", "Anki 到期", "\uE8FD", "#0EA5A4"),
            };
            foreach (var (src, name, glyph, color) in demo)
            {
                ServiceHub.Inbox.Push(new Services.PluginMessage
                {
                    Source = src,
                    SourceName = name,
                    Title = name,
                    Body = "示例消息",
                    Level = "info",
                    IconGlyph = glyph,
                    IconColor = color,
                });
            }

            // 最后推一条带图片的（提示区展示的是最新一条消息的自定义动画/图片）
            var demoImg = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FF14DJJ.jpg");
            if (System.IO.File.Exists(demoImg))
            {
                ServiceHub.Inbox.Push(new Services.PluginMessage
                {
                    Source = "pic",
                    SourceName = "图片通知",
                    Title = "带图片的消息",
                    Body = "插件自带图片",
                    IconGlyph = "\uE91B",
                    IconColor = "#22C55E",
                    NotifyImage = demoImg,
                });
            }
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

        // DWM 模式下窗口每帧贴着药丸改尺寸，指针可能暂时落到"新窗口矩形之外"，
        // WPF 会误报 MouseLeave → 收起 → 又回到窗口上展开，来回抖。
        // 注意：不能用 Mouse.GetPosition —— 指针已离开窗口时它返回的是**缓存的旧位置**，
        // 会让这里永远判定"还在岛内"，于是彻底收不起来（踩过）。
        // 改用系统真实光标位置与本体的屏幕矩形比较。
        if (CursorInBody()) return;
        // 页面/导航内容被重建时（例如消息磁贴刷新）WPF 会对被移除的元素误报 MouseLeave，
        // 指针其实还在页面上：这种情况也不能收。
        if (_pageHost.IsMouseOver || _nav.IsMouseOver) return;
        if (ServiceHub.Config.Get("island.pinned", false)) return;
        // 菜单/对话框打开期间不收
        if (_uiLocks.Count > 0) return;
        // 正在输入（输入框有键盘焦点）不收
        if (IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    /// <summary>系统真实光标是否落在本体（药丸）矩形内。</summary>
    private bool CursorInBody()
    {
        if (!GetCursorPos(out var pt)) return false;
        var body = BodyScreenRect();          // DIP
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return body.Contains(new Point(pt.X / sx, pt.Y / sy));
    }

    // ---------------- 收起态新消息提示 ----------------

    /// <summary>提示展示时长（毫秒，可配置）。</summary>
    private int NotifyDurationMs =>
        Math.Clamp(ServiceHub.Config.Get("island.notify_duration_ms", 4000), 1200, 15000);

    private bool NotifyTickerStyle =>
        string.Equals(ServiceHub.Config.Get("island.notify_style", "popup"), "ticker",
            StringComparison.OrdinalIgnoreCase);

    private void OnInboxNotifyChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _chrome.SetUnread(ServiceHub.Inbox.UnreadCount());

                var allowCustom = ServiceHub.Config.Get("island.notify_plugin_anim", true);
                // 只提示"刚到的"消息，避免启动/清理时把历史消息全弹一遍
                foreach (var msg in ServiceHub.Inbox.Items
                             .Where(x => !x.Read && (DateTime.Now - x.Time).TotalSeconds < 6)
                             .Reverse())
                {
                    if (!_notifySeen.Add(msg)) continue;
                    var body = string.IsNullOrWhiteSpace(msg.Body) ? msg.Title
                             : string.IsNullOrWhiteSpace(msg.Title) ? msg.Body
                             : $"{msg.Title} · {msg.Body}";
                    var text = $"{msg.DisplaySource}  {body}";
                    var draw = allowCustom ? msg.NotifyDraw : null;
                    var image = allowCustom && draw == null ? msg.NotifyImage : null;

                    if (NotifyTickerStyle)
                    {
                        _notifyQueue.Enqueue((text, draw));
                    }
                    else
                    {
                        // 弹出模式：同时到达的多条一次排开（最多 3 行），超出顶掉最旧的一条
                        while (_popupLineTimes.Count >= 3)
                        {
                            _chrome.PopNotifyLine();
                            _popupLineTimes.RemoveAt(0);
                        }
                        _chrome.PushNotifyLine(text, draw);
                        _chrome.SetNotifyImage(image);
                        _popupLineTimes.Add(DateTime.Now);
                    }
                }

                if (NotifyTickerStyle && !_notifyShowing) AdvanceNotify();
                else if (!NotifyTickerStyle && _popupLineTimes.Count > 0) _notifySweep.Start();
            }
            catch (Exception ex) { App.LogCrash("notify-ui", ex); }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>弹出模式：把超过停留时长的行逐条收起（最早的一行先收）。</summary>
    private void SweepPopupLines()
    {
        try
        {
            var duration = NotifyDurationMs;
            var removed = false;
            while (_popupLineTimes.Count > 0 &&
                   (DateTime.Now - _popupLineTimes[0]).TotalMilliseconds >= duration)
            {
                _chrome.PopNotifyLine();
                _popupLineTimes.RemoveAt(0);
                removed = true;
            }
            if (_popupLineTimes.Count == 0)
            {
                _notifySweep.Stop();
                _chrome.SetNotifyImage(null);
            }
            else if (removed)
            {
                _notifySweep.Stop();
                _notifySweep.Start();
            }
        }
        catch (Exception ex) { App.LogCrash("notify-sweep", ex); }
    }

    /// <summary>展示下一条；队列空则收起提示。</summary>
    private void AdvanceNotify()
    {
        _notifyTimer.Stop();

        // 先收掉上一条
        if (_notifyShowing)
        {
            if (NotifyTickerStyle) _chrome.SetTicker("");
            else if (NotifyTickerStyle == false) _chrome.PopNotifyLine();
        }

        if (_notifyQueue.Count == 0)
        {
            _notifyShowing = false;
            if (!NotifyTickerStyle) _chrome.ClearNotify();
            return;
        }

        var (text, draw) = _notifyQueue.Dequeue();
        _chrome.NotifyTickerMode = NotifyTickerStyle;
        _notifyShowing = true;
        if (NotifyTickerStyle) _chrome.SetTicker(text, draw);
        else _chrome.PushNotifyLine(text, draw);

        _notifyTimer.Interval = TimeSpan.FromMilliseconds(NotifyDurationMs);
        _notifyTimer.Start();
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
            _chrome.ExpandedHeight = () => CurrentPageHeight() + ChromeExtra;
            _ = CurrentPageHeight();
            FitWindowToPages();
        }
        catch (Exception ex) { App.LogCrash("prewarm pages", ex); }
    }

    // ---------------- 拖放：把文件/程序拖到岛上加入启动栏 ----------------

    private void SetupFileDrop()
    {
        AllowDrop = true;

        PreviewDragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            // 收起态被拖入时自动展开（否则没有可放的位置）
            if (!_chrome.IsExpanded) Expand();
        };

        Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            e.Handled = true;

            ShowPage("home");
            if (_pages.TryGetValue("home", out var page) && page is HomePage home)
                home.AddDroppedPaths(files);
        };
    }

    private void StartTicker()
    {
        _dragHold.Tick += (_, _) =>
        {
            _dragHold.Stop();
            if (!_holdPending) return;
            _holdPending = false;
            // 仍按着左键且指针在本体内 → 视为拖拽
            CollapseToCursor();
        };
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
        // 只要"尺寸"弹簧到位就恢复玻璃，不要等内容淡入弹簧（那个要 ~1s，
        // 于是模糊会延迟约 1 秒才出现）。
        if (_chrome.SystemGlass && SizeSettled()) EndGlassAnim();

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
        _chrome.ExpandedHeight = () => CurrentPageHeight() + ChromeExtra;

        // 展开后提示区没有意义，先清掉
        _notifyQueue.Clear();
        _popupLineTimes.Clear();
        _notifySweep.Stop();
        _notifyTimer.Stop();
        _notifyShowing = false;
        _chrome.ClearNotify();
        _chrome.SetNotifyImage(null);

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
    /// <summary>
    /// 抓屏/实色模式窗口尺寸固定（动画期间不改尺寸才流畅），但高度必须能容纳
    /// 最高的那个页面，否则展开到高页面时本体底部会被窗口矩形裁掉
    /// （表现为底部圆弧和描边线消失几个像素）。
    /// </summary>
    private void FitWindowToPages()
    {
        if (_chrome.SystemGlass) return;      // DWM 模式窗口贴合药丸，不需要

        double maxH = 0;
        foreach (var page in _pages.Values)
        {
            page.Measure(new Size(636, 2000));
            // page.Height 是上一次 arrange 的高度（展开时会被拉满可用空间），
            // DesiredSize 是内容自然高度，两者取大才不会算小。
            maxH = Math.Max(maxH, Math.Max(page.DesiredSize.Height, page.Height));
        }
        foreach (var h in _pageHeights.Values) maxH = Math.Max(maxH, h);

        // +10 余量：本体高度恰好顶到窗口边缘时，底部圆弧和描边会被窗口裁掉几个像素
        var need = maxH + ChromeExtra + 2 * IslandChrome.WindowPad + 10;

        var target = Math.Max(FixedWindowHeight, Math.Ceiling(need));
        if (Math.Abs(Height - target) < 1) return;

        Height = target;
        PositionWindow();
    }

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
        if (_trayAdded) return;
        _trayIcon = LoadAppIcon();
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = WmTrayIcon,
            szTip = "wisland 灵动岛",
            hIcon = _trayIcon,
        };
        _trayAdded = Shell_NotifyIcon(NimAdd, ref nid);
        if (!_trayAdded) App.DebugNote("tray: Shell_NotifyIcon(NIM_ADD) 失败，err=" + Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// 移除托盘图标并释放图标句柄。
    /// 不做这一步的后果：程序退出（尤其被强杀/崩溃）后任务栏留下一枚"幽灵图标"，
    /// 必须把鼠标划过去才会消失。
    /// </summary>
    private void DestroyTray()
    {
        if (_trayAdded)
        {
            try
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                    hWnd = _hwnd,
                    uID = 1,
                };
                Shell_NotifyIcon(NimDelete, ref nid);
            }
            catch (Exception ex) { App.DebugNote("tray: 删除图标失败 " + ex.Message); }
            _trayAdded = false;
        }

        if (_trayIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_trayIcon); } catch { }
            _trayIcon = IntPtr.Zero;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        DestroyTray();
        base.OnClosed(e);
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var path = AppPaths.ResourcePath("1002.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile);
                if (h != IntPtr.Zero) return h;
            }
            var exe = Environment.ProcessPath ?? Path.Combine(AppPaths.BaseDir, "Wisland.exe");
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
                          Path.Combine(AppPaths.BaseDir, "Wisland.exe");
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
            // 只认"抬起"事件：早期同时处理 0x0204(RBUTTONDOWN) 与 0x0205(RBUTTONUP)，
            // 一次右键会弹两遍菜单（第一次被第二次顶掉，表现为菜单闪一下）。
            if (ev == 0x0205) ShowTrayMenu();          // WM_RBUTTONUP
            else if (ev == 0x0202) ToggleIsland();     // WM_LBUTTONUP
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

    // ---- 收起态新消息提示 ----
    private readonly HashSet<Services.PluginMessage> _notifySeen = new();
    private readonly Queue<(string Text, Action<System.Windows.Media.DrawingContext, Rect, double>? Draw)> _notifyQueue = new();
    private readonly System.Windows.Threading.DispatcherTimer _notifyTimer =
        new() { Interval = TimeSpan.FromMilliseconds(4000) };
    private bool _notifyShowing;

    // 弹出模式：每一行的到达时间（与 chrome 中显示的行一一对应），到时间逐条收起
    private readonly List<DateTime> _popupLineTimes = new();
    private readonly System.Windows.Threading.DispatcherTimer _notifySweep =
        new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>本体尺寸是否已经到目标（用于尽早恢复 DWM 模糊）。</summary>
    private bool SizeSettled()
    {
        var (w, h) = _chrome.BodySize;
        var (tw, th) = _chrome.BodyTarget;
        return Math.Abs(w - tw) < 0.8 && Math.Abs(h - th) < 0.8;
    }

    private readonly System.Windows.Threading.DispatcherTimer _glassResume =
        new() { Interval = TimeSpan.FromMilliseconds(90) };

    /// <summary>当前 accent 变体（true = 经典 blur-behind 真模糊；false = 亚克力）。</summary>
    private bool _glassBlurBehind = true;

    /// <summary>玻璃模式：system（DWM 动态模糊）/ capture（抓屏静态模糊）/ solid（实色无模糊）。</summary>
    private string _glassMode = "system";

    /// <summary>按 config 应用玻璃模式（启动与配置变更时调用）。</summary>
    private void ApplyGlassMode()
    {
        var cfg = ServiceHub.Config.Get("island.glass", "capture");
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

            // 从 DWM 模式切回来时，窗口还停在"贴合药丸"的小尺寸上，
            // 而抓屏/实色模式需要固定尺寸窗口（700×max）——不恢复的话展开后
            // 本体底部会被这个小窗口裁掉（表现为底部圆弧和描边消失）。
            Width = FixedWindowWidth;
            Height = FixedWindowHeight;
            FitWindowToPages();
            PositionWindow();
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


    // ---------------- 毛玻璃 ----------------
    private static void Try(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { App.LogCrash($"MainWindow init [{name}]", ex); }
    }
}
