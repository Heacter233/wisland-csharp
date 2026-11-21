using System;
using System.Collections.Generic;

namespace Wisland.Services;

/// <summary>启动器条目（config.json → launcher[]）。</summary>
public sealed class LauncherItem
{
    public string Name { get; internal set; } = "";
    public string Path { get; internal set; } = "";
    public string Glyph { get; internal set; } = "";
    public string Exe { get; internal set; } = "";

    /// <summary>是否置顶（排在启动栏最前）。</summary>
    public bool Pinned { get; internal set; }

    /// <summary>自定义图标文件路径（.png/.ico/.jpg）；为空则用系统图标。</summary>
    public string Icon { get; internal set; } = "";

    /// <summary>是否为文件夹（影响图标与提示文字）。</summary>
    public bool IsFolder { get; internal set; }

    /// <summary>启动参数（例如 "C:\a.txt" 或 --flag）。</summary>
    public string Args { get; internal set; } = "";
}

/// <summary>
/// 服务定位器：页面通过它访问各服务（WPF 页面在 XAML 里实例化，不便逐个构造注入）。
/// App 启动时一次性填充。
/// </summary>
public static class ServiceHub
{
    public static IConfigService Config { get; internal set; } = null!;
    public static IClipboardService Clipboard { get; internal set; } = null!;
    public static IHotkeyService Hotkey { get; internal set; } = null!;
    public static IMediaService Media { get; internal set; } = null!;
    public static IWeatherService Weather { get; internal set; } = null!;
    public static ITranslationService Translate { get; internal set; } = null!;
    public static ILiveService Live { get; internal set; } = null!;
    public static LyricsService? Lyrics { get; internal set; }
    public static AudioService? Audio { get; internal set; }
    public static BrightnessService? Brightness { get; internal set; }
    public static NotifyService Notify { get; internal set; } = null!;

    /// <summary>插件/外部程序推送文本消息的收件箱（消息页会合并显示）。</summary>
    public static MessageInbox Inbox { get; internal set; } = new();

    /// <summary>消息提示音（默认关闭；按来源单独开启，插件需声明 notify.sound）。</summary>
    public static NotifySoundService NotifySound { get; internal set; } = new();
    public static RadioService Radio { get; internal set; } = null!;
    public static WordbookService Wordbook { get; internal set; } = null!;

    /// <summary>语音模型下载管理器（下载状态与生命周期独立于页面）。</summary>
    public static ModelManager Models { get; internal set; } = null!;

    /// <summary>请求展开到指定页（"" 表示保持当前页）。由 MainWindow 实现。</summary>
    public static Action<string>? RequestExpand { get; internal set; }

    /// <summary>请求收起。</summary>
    public static Action? RequestCollapse { get; internal set; }

    /// <summary>请求重读悬停/收起延时。</summary>
    public static Action? RequestTimings { get; internal set; }

    /// <summary>请求重新贴边定位。</summary>
    public static Action? RequestReanchor { get; internal set; }

    /// <summary>请求生效可穿透。</summary>
    public static Action<bool>? RequestPassthrough { get; internal set; }

    /// <summary>请求设置开机自启。</summary>
    public static Action<bool>? RequestAutostart { get; internal set; }

    /// <summary>请求切换收起态视图（clock/pet/music）。</summary>
    public static Action<string>? RequestCompactView { get; internal set; }

    /// <summary>请求重新注册全局热键。</summary>
    public static Action? RequestReloadHotkeys { get; internal set; }

    /// <summary>UI 锁（菜单/对话框/输入中）：加锁期间窗口不自动收起。</summary>
    public static Action<string>? PushUiLock { get; internal set; }
    public static Action<string>? PopUiLock { get; internal set; }

    /// <summary>请求重绘当前页面（插件修改了页面视觉树后调用）。</summary>
    public static Action? RequestRepaint { get; internal set; }

    /// <summary>
    /// 请求重载插件：参数为 null/空表示重载全部，否则只重载该 id。
    /// 由 MainWindow 实现（卸载/加载 + 重建插件页与设置分组）。
    /// </summary>
    public static Action<string?>? RequestReloadPlugins { get; internal set; }

    /// <summary>请求重新应用主题。</summary>
    public static Action? RequestThemeRefresh { get; internal set; }

    /// <summary>重新应用毛玻璃模式（island.glass / glass_accent / glass_fog / glass_corner 变化时）。</summary>
    public static Action? RequestGlass { get; internal set; }

    /// <summary>请求按当前页面高度重新计算岛高度（页面内容变化时调用）。</summary>
    public static Action? RequestRetargetHeight { get; internal set; }

    /// <summary>当前主题名（用于页面内自绘控件取色）。</summary>
    public static event EventHandler? ThemeChanged;

    public static void RaiseThemeChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);
}