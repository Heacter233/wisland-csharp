using System;
using System.Collections.Generic;

namespace Wisland.Services;

/// <summary>启动器条目（config.json → launcher[]）。</summary>
public sealed class LauncherItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Exe { get; set; } = "";

    /// <summary>是否置顶（排在启动栏最前）。</summary>
    public bool Pinned { get; set; }

    /// <summary>自定义图标文件路径（.png/.ico/.jpg）；为空则用系统图标。</summary>
    public string Icon { get; set; } = "";

    /// <summary>是否为文件夹（影响图标与提示文字）。</summary>
    public bool IsFolder { get; set; }
}

/// <summary>
/// 服务定位器：页面通过它访问各服务（WPF 页面在 XAML 里实例化，不便逐个构造注入）。
/// App 启动时一次性填充。
/// </summary>
public static class ServiceHub
{
    public static IConfigService Config { get; set; } = null!;
    public static IClipboardService Clipboard { get; set; } = null!;
    public static IHotkeyService Hotkey { get; set; } = null!;
    public static IMediaService Media { get; set; } = null!;
    public static IWeatherService Weather { get; set; } = null!;
    public static ITranslationService Translate { get; set; } = null!;
    public static ILiveService Live { get; set; } = null!;
    public static LyricsService? Lyrics { get; set; }
    public static AudioService? Audio { get; set; }
    public static BrightnessService? Brightness { get; set; }
    public static NotifyService Notify { get; set; } = null!;

    /// <summary>插件/外部程序推送文本消息的收件箱（消息页会合并显示）。</summary>
    public static MessageInbox Inbox { get; set; } = new();
    public static RadioService Radio { get; set; } = null!;
    public static WordbookService Wordbook { get; set; } = null!;

    /// <summary>请求展开到指定页（"" 表示保持当前页）。由 MainWindow 实现。</summary>
    public static Action<string>? RequestExpand { get; set; }

    /// <summary>请求收起。</summary>
    public static Action? RequestCollapse { get; set; }

    /// <summary>请求重读悬停/收起延时。</summary>
    public static Action? RequestTimings { get; set; }

    /// <summary>请求重新贴边定位。</summary>
    public static Action? RequestReanchor { get; set; }

    /// <summary>请求生效可穿透。</summary>
    public static Action<bool>? RequestPassthrough { get; set; }

    /// <summary>请求设置开机自启。</summary>
    public static Action<bool>? RequestAutostart { get; set; }

    /// <summary>请求切换收起态视图（clock/pet/music）。</summary>
    public static Action<string>? RequestCompactView { get; set; }

    /// <summary>请求重新注册全局热键。</summary>
    public static Action? RequestReloadHotkeys { get; set; }

    /// <summary>UI 锁（菜单/对话框/输入中）：加锁期间窗口不自动收起。</summary>
    public static Action<string>? PushUiLock { get; set; }
    public static Action<string>? PopUiLock { get; set; }

    /// <summary>请求重新应用主题。</summary>
    public static Action? RequestThemeRefresh { get; set; }

    /// <summary>重新应用毛玻璃模式（island.glass / glass_accent / glass_fog / glass_corner 变化时）。</summary>
    public static Action? RequestGlass { get; set; }

    /// <summary>请求按当前页面高度重新计算岛高度（页面内容变化时调用）。</summary>
    public static Action? RequestRetargetHeight { get; set; }

    /// <summary>当前主题名（用于页面内自绘控件取色）。</summary>
    public static event EventHandler? ThemeChanged;

    public static void RaiseThemeChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);
}