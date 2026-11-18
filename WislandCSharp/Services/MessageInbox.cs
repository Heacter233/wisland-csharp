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

    /// <summary>最多保留多少条。</summary>
    public int Capacity { get; set; } = 50;

    /// <summary>有新消息或条目变化时触发（可能在非 UI 线程）。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PluginMessage> Items
    {
        get { lock (_lock) return _items.ToArray(); }
    }

    /// <summary>推送一条消息。</summary>
    public void Push(PluginMessage message) => Push(message, notify: true);

    /// <summary>快捷方法：只给标题与正文。</summary>
    public void Push(string source, string title, string body = "", string level = "info")
        => Push(new PluginMessage { Source = source, Title = title, Body = body, Level = level });

    private void Push(PluginMessage message, bool notify)
    {
        if (message == null) return;
        lock (_lock)
        {
            _items.Insert(0, message);
            if (_items.Count > Math.Max(1, Capacity))
                _items.RemoveRange(Capacity, _items.Count - Capacity);
        }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
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
