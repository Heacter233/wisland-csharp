using System;
using System.Collections.Generic;
using System.Linq;
using Wisland.Plugins;

namespace Wisland.Services;

/// <summary>
/// 一个插件可以提供给设置页的配置面板。
/// </summary>
public sealed class PluginSettingsSection
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>构建面板内容（由插件实现，宿主只负责放位置）。在 UI 线程延迟调用。</summary>
    public Func<System.Windows.FrameworkElement> Build { get; init; } = () => new System.Windows.Controls.StackPanel();
}

/// <summary>插件声明与运行时状态（设置页展示用）。</summary>
public sealed class PluginInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public PluginSettingsSection? Settings { get; init; }

    /// <summary>加载状态。</summary>
    public PluginState State { get; init; } = PluginState.Loaded;

    /// <summary>加载失败原因（State = Failed 时有值）。</summary>
    public string Error { get; init; } = "";

    /// <summary>清单里声明的权限。</summary>
    public string[] Permissions { get; init; } = Array.Empty<string>();

    /// <summary>该插件会向消息页推送文本消息（声明后宿主可在设置页展示，并用于告警过滤）。</summary>
    public bool PushesMessages { get; init; }

    public bool IsLoaded => State == PluginState.Loaded;
}

/// <summary>
/// 插件注册表：保存已加载插件（含失败/禁用），供设置页展示。
///
/// 正常流程由 <see cref="PluginLoader"/> 在启动时填充；
/// 也可以在宿主内部直接 <see cref="Register"/>（例如把内置功能包装成插件面板）。
/// </summary>
public static class PluginRegistry
{
    private static readonly List<PluginInfo> _plugins = new();
    private static readonly object _lock = new();

    public static event EventHandler? Changed;

    public static IReadOnlyList<PluginInfo> Plugins
    {
        get { lock (_lock) return _plugins.ToArray(); }
    }

    /// <summary>已成功加载的插件数（不含失败/禁用）。</summary>
    public static int LoadedCount
    {
        get { lock (_lock) return _plugins.Count(p => p.IsLoaded); }
    }

    public static bool Register(PluginInfo plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Id)) return false;
        lock (_lock)
        {
            if (_plugins.Exists(p => p.Id == plugin.Id)) return false;
            _plugins.Add(plugin);
        }
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>注册或覆盖（重新加载时用）。</summary>
    public static void RegisterOrReplace(PluginInfo plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Id)) return;
        lock (_lock)
        {
            _plugins.RemoveAll(p => p.Id == plugin.Id);
            _plugins.Add(plugin);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Unregister(string id)
    {
        lock (_lock) _plugins.RemoveAll(p => p.Id == id);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear()
    {
        lock (_lock) _plugins.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>按 id 取插件。</summary>
    public static PluginInfo? Find(string id)
    {
        lock (_lock) return _plugins.Find(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 尚未实现、但已在计划内的能力。设置页据此显示"待实现/未接入"，
/// 避免这些功能在界面上"看起来能用其实没实现"。
/// </summary>
public static class PendingFeatures
{
    public sealed class Item
    {
        public string Title { get; init; } = "";
        public string State { get; init; } = "待实现";
        public string Note { get; init; } = "";
    }

    public static IReadOnlyList<Item> All { get; } = new[]
    {
        new Item
        {
            Title = "液态形变",
            State = "待实现",
            Note = "原版的液桥/拉丝过渡与贴边内凹圆角；目前只有弹簧缩放与按压挤压",
        },
        new Item
        {
            Title = "Clash 集成",
            State = "待插件",
            Note = "可由插件通过 network.local + messages.push 接入外部控制器 API",
        },
        new Item
        {
            Title = "股票行情",
            State = "待插件",
            Note = "可由插件通过 network + messages.push 实现",
        },
    };
}