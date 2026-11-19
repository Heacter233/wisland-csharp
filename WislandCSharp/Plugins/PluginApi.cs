using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wisland.Services;

namespace Wisland.Plugins;

/// <summary>
/// 插件可声明的权限。未声明的权限，宿主侧调用会被拒绝并记录日志。
///
/// 注意（重要）：插件与宿主**同进程**运行，这里的权限是"声明 + 守门"，
/// 不是安全沙箱。真正的隔离需要独立进程，属于后续工作。
/// 详见 docs/plugins.md「信任模型」。
/// </summary>
public static class PluginPermissions
{
    /// <summary>向消息页推送消息（ServiceHub.Inbox）。</summary>
    public const string MessagesPush = "messages.push";

    /// <summary>提供设置面板（设置页会出现该插件的分组）。</summary>
    public const string SettingsPanel = "ui.settings";

    /// <summary>在收起态胶囊内自绘（NotifyDraw）。</summary>
    public const string CompactDraw = "ui.compact";

    /// <summary>访问外网（http/https）。</summary>
    public const string Network = "network";

    /// <summary>只访问本机地址（127.0.0.1 / localhost），例如 Clash 外部控制器。</summary>
    public const string NetworkLocal = "network.local";

    /// <summary>启动进程 / 打开文件。</summary>
    public const string ProcessStart = "process.start";

    /// <summary>读取剪贴板内容。</summary>
    public const string ClipboardRead = "clipboard.read";

    /// <summary>读写用户任意文件（默认只能读写自己的插件目录与数据目录）。</summary>
    public const string FilesUser = "files.user";

    /// <summary>通知使用自定义图片（NotifyImage）。</summary>
    public const string NotifyImage = "notify.image";

    /// <summary>读取系统信息（CPU/内存/电池等）。</summary>
    public const string SystemInfo = "system.info";

    public static readonly IReadOnlyList<string> All = new[]
    {
        MessagesPush, SettingsPanel, CompactDraw, Network, NetworkLocal,
        ProcessStart, ClipboardRead, FilesUser, NotifyImage, SystemInfo,
    };

    public static bool IsKnown(string permission) =>
        !string.IsNullOrWhiteSpace(permission) &&
        new List<string>((IEnumerable<string>)All)
            .Exists(p => string.Equals(p, permission, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 插件主接口。实现类必须有无参构造函数，且在 OnLoad 内完成初始化。
/// </summary>
public interface IWislandPlugin
{
    /// <summary>
    /// 宿主加载插件时调用（在后台线程，超时 8 秒）。
    /// 只做初始化与注册，不要在这里做耗时 IO/网络；需要 UI 请用 <see cref="IPluginContext.Ui"/> 切回。
    /// </summary>
    void OnLoad(IPluginContext context);

    /// <summary>
    /// 宿主卸载插件时调用：释放定时器、取消订阅、停止线程。
    /// 抛出异常会被记录，但不会影响宿主退出。
    /// </summary>
    void OnUnload();
}

/// <summary>
/// 插件可用的宿主能力。这是插件与宿主的**唯一**交互面。
/// </summary>
public interface IPluginContext
{
    /// <summary>本插件清单。</summary>
    PluginManifest Manifest { get; }

    /// <summary>插件自己的配置（持久化到 data/plugins/&lt;id&gt;/config.json，键空间独立）。</summary>
    IPluginConfig Config { get; }

    /// <summary>日志（写入 data/plugins/&lt;id&gt;/plugin.log）。</summary>
    IPluginLogger Log { get; }

    /// <summary>插件数据目录（可读写，已创建）。</summary>
    string DataDirectory { get; }

    /// <summary>插件安装目录（只读，宿主不会清理）。</summary>
    string PluginDirectory { get; }

    /// <summary>宿主 UI 线程调度器。</summary>
    Dispatcher Ui { get; }

    /// <summary>宿主退出/插件卸载时取消。</summary>
    CancellationToken ShutdownToken { get; }

    /// <summary>宿主版本（用于能力判断）。</summary>
    string HostVersion { get; }

    /// <summary>是否声明了某权限。</summary>
    bool Has(string permission);

    /// <summary>推送一条消息到消息页（需要 messages.push）。</summary>
    void PushMessage(PluginMessage message);

    /// <summary>请求展开灵动岛到指定页（"" 表示当前页）。</summary>
    void RequestExpand(string pageKey = "");

    /// <summary>请求收起。</summary>
    void RequestCollapse();

    /// <summary>页面内容高度变化后请求重算岛高度。</summary>
    void RequestRetargetHeight();

    /// <summary>
    /// 注册一个宿主托管的定时器（卸载时自动释放）。
    /// 最小间隔 100ms；<paramref name="runOnUi"/> 为 true 时回调在 UI 线程。
    /// </summary>
    IDisposable RegisterTimer(TimeSpan interval, Action tick, bool runOnUi = true);

    /// <summary>
    /// 注册一个设置分组（需要 ui.settings）。
    /// <paramref name="build"/> 会被延迟调用（在 UI 线程），可以安全创建 WPF 控件。
    /// </summary>
    void RegisterSettingsSection(string title, string subtitle, Func<FrameworkElement> build);
}

/// <summary>插件独立配置：键空间与宿主 config.json 分离。</summary>
public interface IPluginConfig
{
    T Get<T>(string key, T fallback = default!);
    void Set<T>(string key, T value, bool save = true);
    void Save();
    event EventHandler? Changed;
}

/// <summary>插件日志。</summary>
public interface IPluginLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
