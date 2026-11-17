using System;
using System.Collections.Generic;

namespace Wisland.Services;

/// <summary>
/// 一个插件可以提供给设置页的配置面板。
/// </summary>
public sealed class PluginSettingsSection
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>构建面板内容（由插件实现，宿主只负责放位置）。</summary>
    public Func<System.Windows.FrameworkElement> Build { get; init; } = () => new System.Windows.Controls.StackPanel();
}

/// <summary>插件声明。</summary>
public sealed class PluginInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string Description { get; init; } = "";
    public PluginSettingsSection? Settings { get; init; }
}

/// <summary>
/// 插件注册表（预留接口）。
///
/// 设计目的：让后续功能（例如 Clash 集成、歌词源、通知源）以插件形式接入，
/// 而不必改动主程序。设置页会遍历这里的条目，为每个插件追加一个设置分组。
///
/// 当前主程序尚未加载任何外部插件，<see cref="Plugins"/> 为空，
/// 设置页会显示"暂无插件"。接入方式：
///   PluginRegistry.Register(new PluginInfo { ... Settings = new PluginSettingsSection { Build = () => panel } });
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

    public static void Unregister(string id)
    {
        lock (_lock) _plugins.RemoveAll(p => p.Id == id);
        Changed?.Invoke(null, EventArgs.Empty);
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
            State = "未接入",
            Note = "需要读取 Clash 外部控制器 API，暂未对接",
        },
        new Item
        {
            Title = "插件系统",
            State = "接口就绪",
            Note = "PluginRegistry 已提供注册与设置面板挂载点，暂无已加载插件",
        },
    };
}