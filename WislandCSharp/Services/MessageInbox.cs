using System;
using System.Collections.Generic;
using System.Linq;

namespace Wisland.Services;

/// <summary>一条由插件（或外部脚本）推送进消息页的条目。</summary>
public sealed class PluginMessage
{
    /// <summary>来源标识，用于分组/图标（如 "clash"、"bilibili"）。</summary>
    public string Source { get; init; } = "plugin";
    /// <summary>来源显示名（如 "Clash"）。留空时用 Source。</summary>
    public string SourceName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>info / warn / error，用于配色。</summary>
    public string Level { get; init; } = "info";

    /// <summary>是否已读（点磁贴/点条目后置为已读）。</summary>
    public bool Read { get; set; }

    /// <summary>
    /// 磁贴图标字形（Segoe MDL2 Assets 码位，如 "\uE774"）。
    /// 留空则用通用对话气泡图标。
    /// </summary>
    public string? IconGlyph { get; init; }

    /// <summary>
    /// 磁贴图标底色（HTML 十六进制，如 "#E8544E"）。
    /// 留空则按 <see cref="Level"/> 取色（info 蓝 / warn 黄 / error 红）。
    /// </summary>
    public string? IconColor { get; init; }

    /// <summary>
    /// 内置类型标识（qq / wechat / mail）：点磁贴时会打开对应程序。
    /// 插件消息留空即可。
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// 插件自定义通知动画（可选）：在收起态胶囊内绘制。
    /// 参数：绘制上下文、可用区域、经过秒数。设置项 island.notify_plugin_anim = false 时会被忽略。
    /// </summary>
    public Action<System.Windows.Media.DrawingContext, System.Windows.Rect, double>? NotifyDraw { get; init; }

    /// <summary>插件自定义通知用的图片（可选，绝对路径；与 NotifyDraw 二选一）。</summary>
    public string? NotifyImage { get; init; }

    /// <summary>点击条目要打开的链接（可选）。</summary>
    public string? Uri { get; init; }

    /// <summary>点击条目要执行的回调（可选，进程内）。</summary>
    public Action? Action { get; init; }

    public string DisplaySource => string.IsNullOrWhiteSpace(SourceName) ? Source : SourceName;
}

/// <summary>
/// 插件消息收件箱（预留给插件/外部程序把文本消息推进消息页）。
///
/// 设计要点：
///   - 与系统通知（<see cref="NotifyService"/>）分开放，互不影响；
///     消息页会把两边合并按时间排序显示。
///   - 只保留内存里的最近 N 条（默认 50），不做持久化，重启即清空。
///   - 线程安全：插件可能在任意线程调用 Push，UI 通过 <see cref="Changed"/> 事件回到 UI 线程刷新。
///
/// 接入方式（插件一侧，一行即可）：
/// <code>
/// ServiceHub.Inbox.Push(new PluginMessage { Source = "clash", SourceName = "Clash", Title = "规则已更新", Body = "..." });
/// </code>
/// 也可以通过 <see cref="PluginRegistry"/> 声明的能力让宿主知道该插件会推送消息。
/// </summary>
public sealed class MessageInbox
{
    private readonly object _lock = new();
    private readonly List<PluginMessage> _items = new();

    /// <summary>内置来源（系统通知镜像用）：插件不得冒充它们。</summary>
    private static readonly HashSet<string> BuiltinSources =
        new(StringComparer.OrdinalIgnoreCase) { "qq", "wechat", "mail", "other" };

    /// <summary>已加载插件注册的来源 id（由 PluginLoader 在装载/卸载时维护）。</summary>
    private readonly HashSet<string> _pluginSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册一个"允许推送"的插件来源 id。</summary>
    public void RegisterPluginSource(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock) _pluginSources.Add(id);
    }

    /// <summary>注销插件来源 id（插件卸载/重载时调用）。</summary>
    public void UnregisterPluginSource(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_lock) _pluginSources.Remove(id);
    }

    /// <summary>该来源是否被允许（内置来源或已加载插件）。</summary>
    public bool IsKnownSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        lock (_lock)
        {
            return BuiltinSources.Contains(source) || _pluginSources.Contains(source);
        }
    }

    /// <summary>
    /// 宿主内部推送（系统通知镜像等）：跳过来源校验。
    /// 插件不应调用它——插件走 IPluginContext.PushMessage（会强制把 Source 改成插件自己的 id）。
    /// </summary>
    internal void PushTrusted(PluginMessage message) => PushCore(message, notify: true, strict: false);

    /// <summary>最多保留多少条。</summary>
    public int Capacity { get; set; } = 50;

    /// <summary>容量上限：防止插件把 Capacity 设成天文数字后无限吃内存。</summary>
    private const int MaxCapacity = 500;

    /// <summary>有新消息或条目变化时触发（可能在非 UI 线程）。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PluginMessage> Items
    {
        get { lock (_lock) return _items.ToArray(); }
    }

    /// <summary>
    /// 推送一条消息。
    /// 来源必须是内置来源（qq/wechat/mail/other）或已加载插件的 id，
    /// 否则视为伪造（例如插件谎称自己是 QQ 去借用内置来源的提示音与磁贴），直接丢弃并记日志。
    /// </summary>
    public void Push(PluginMessage message) => PushCore(message, notify: true, strict: true);

    /// <summary>快捷方法：只给标题与正文。</summary>
    public void Push(string source, string title, string body = "", string level = "info")
        => Push(new PluginMessage { Source = source, Title = title, Body = body, Level = level });

    private void PushCore(PluginMessage message, bool notify, bool strict)
    {
        if (message == null) return;

        if (strict && !IsKnownSource(message.Source))
        {
            App.DebugNote($"inbox: 拒绝来源不明的消息 Source='{message.Source}'（伪造来源或插件未注册）");
            return;
        }

        lock (_lock)
        {
            _items.Insert(0, message);
            // Capacity 可能被插件/配置改成 0 或负数：先夹到合法区间，
            // 否则 RemoveRange(负数, ...) 会抛 ArgumentOutOfRange（Push 被插件调用时会带崩宿主）。
            var cap = Math.Clamp(Capacity, 1, MaxCapacity);
            if (_items.Count > cap) _items.RemoveRange(cap, _items.Count - cap);
        }
        if (notify)
        {
            // 提示音：是否真正播放由 NotifySoundService 按开关/权限/冷却决定
            try { ServiceHub.NotifySound.OnMessage(message.Source); } catch { }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>删除某一条。</summary>
    public void Remove(PluginMessage message)
    {
        lock (_lock)
        {
            if (!_items.Remove(message)) return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空某个来源；source 为空则清空全部。</summary>
    public void Clear(string? source = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(source)) _items.Clear();
            else _items.RemoveAll(m => string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把某条消息标为已读。</summary>
    public void MarkRead(PluginMessage message)
    {
        if (message == null || message.Read) return;
        message.Read = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把某个来源（或全部）标为已读。</summary>
    public void MarkRead(string? source = null)
    {
        lock (_lock)
        {
            foreach (var m in _items)
                if (string.IsNullOrEmpty(source) || string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase))
                    m.Read = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>未读总数。</summary>
    public int UnreadCount()
    {
        lock (_lock) return _items.Count(m => !m.Read);
    }

    /// <summary>按来源统计条数（消息页顶部磁贴用）。</summary>
    public Dictionary<string, int> Counts()
    {
        lock (_lock)
        {
            return _items
                .GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
