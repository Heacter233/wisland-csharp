using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Windows.Media;
using Wisland.Plugins;

namespace Wisland.Services;

/// <summary>
/// 消息提示音。
///
/// 设计要点：
///   - **默认关闭**：总开关 <c>notify.sound.enabled</c> 默认 false，内置来源（QQ/微信/邮件）
///     与插件来源都各自有独立开关，默认 false —— 想要哪一路响就单独打开。
///   - **插件需要声明**：插件来源只有在 plugin.json 声明了 <c>notify.sound</c> 权限后，
///     才可能被播放（否则即使开关打开也不响，并在日志里记 WRN），避免"悄悄放声音"。
///   - **冷却**：全局冷却 <c>notify.sound.cooldown_ms</c>（默认 3000ms），
///     一批消息同时到达时不会连续轰炸。
///   - **声音来源**：默认用系统提示音（<see cref="SystemSounds.Asterisk"/>，零资源）；
///     配置 <c>notify.sound.file</c> 指向 .wav 时改用该文件（SoundPlayer）。
/// </summary>
public sealed class NotifySoundService
{
    private readonly object _lock = new();
    private DateTime _lastPlay = DateTime.MinValue;
    private string _lastSource = "";

    /// <summary>内置来源：用户可直接开关，无需插件权限。</summary>
    private static readonly string[] BuiltInSources = { "qq", "wechat", "mail" };

    /// <summary>最近一次播放记录（调试/设置页展示用）。</summary>
    public string LastPlayNote { get; private set; } = "（尚未播放）";

    public static bool IsBuiltIn(string source) =>
        !string.IsNullOrWhiteSpace(source) &&
        Array.Exists(BuiltInSources, s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>某来源当前是否允许发声（不看冷却）。</summary>
    public bool IsSourceEnabled(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            return ServiceHub.Config.Get($"notify.sound.source.{source}", false);
        }
        catch { return false; }
    }

    /// <summary>某来源是否"有资格"发声：内置来源恒可；插件来源必须声明 notify.sound。</summary>
    public bool IsSourcePermitted(string source, out string reason)
    {
        reason = "";
        if (IsBuiltIn(source)) return true;

        // 插件来源：按 Source == 插件 id 的约定，查注册表里该插件声明的权限
        var info = PluginRegistry.Find(source);
        if (info == null)
        {
            reason = "来源不是内置通道，也不是已加载的插件";
            return false;
        }
        if (!Array.Exists(info.Permissions,
                p => string.Equals(p, PluginPermissions.NotifySound, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"插件 {source} 未声明 notify.sound 权限";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 收到一条消息时调用：满足条件才播放。
    /// 返回是否真的播放了（便于调试与插件/设置页展示）。
    /// </summary>
    public bool OnMessage(string source)
    {
        try
        {
            if (!ServiceHub.Config.Get("notify.sound.enabled", false)) return false;
            if (!IsSourceEnabled(source))
            {
                App.DebugNote($"sound: 来源 {source} 未开启提示音");
                return false;
            }
            if (!IsSourcePermitted(source, out var reason))
            {
                App.DebugNote($"sound: 拒绝来源 {source} —— {reason}");
                return false;
            }

            var cooldown = Math.Clamp(ServiceHub.Config.Get("notify.sound.cooldown_ms", 3000), 0, 60000);
            lock (_lock)
            {
                var sinceLast = (DateTime.Now - _lastPlay).TotalMilliseconds;
                if (sinceLast < cooldown)
                {
                    App.DebugNote($"sound: 冷却中（{sinceLast:0}ms < {cooldown}ms），跳过 {source}");
                    return false;
                }
                _lastPlay = DateTime.Now;
                _lastSource = source;
            }

            PlayNow();
            LastPlayNote = $"{DateTime.Now:HH:mm:ss} · {source}";
            App.DebugNote($"sound: 播放提示音（来源 {source}）");
            return true;
        }
        catch (Exception ex)
        {
            App.DebugNote("sound: 播放失败 —— " + ex.Message);
            return false;
        }
    }

    /// <summary>直接试听一次（设置页"试听"按钮），不受开关/冷却限制。</summary>
    public void Preview()
    {
        try
        {
            PlayNow();
            LastPlayNote = $"{DateTime.Now:HH:mm:ss} · 试听";
        }
        catch (Exception ex)
        {
            App.DebugNote("sound: 试听失败 —— " + ex.Message);
        }
    }

    /// <summary>随程序发布的内置提示音（Resources/sound.wav）。</summary>
    public static string BuiltInSoundPath =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "sound.wav");

    private void PlayNow()
    {
        var file = "";
        try { file = ServiceHub.Config.Get("notify.sound.file", "") ?? ""; } catch { }

        // 用户自定义 wav 优先
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file) &&
            file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && TryPlayWav(file))
        {
            App.DebugNote("sound: 使用自定义提示音 " + file);
            return;
        }

        var kind = "";
        try { kind = ServiceHub.Config.Get("notify.sound.kind", "builtin") ?? "builtin"; } catch { }

        // 内置提示音（默认）：程序自带的 sound.wav；缺失时退回系统提示音
        if (kind.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(BuiltInSoundPath) && TryPlayWav(BuiltInSoundPath))
            {
                App.DebugNote("sound: 使用内置提示音 " + BuiltInSoundPath);
                return;
            }
            App.DebugNote("sound: 内置提示音缺失，退回系统提示音");
            SystemSounds.Asterisk.Play();
            return;
        }

        switch (kind.ToLowerInvariant())
        {
            case "beep": SystemSounds.Beep.Play(); break;
            case "exclamation": SystemSounds.Exclamation.Play(); break;
            case "question": SystemSounds.Question.Play(); break;
            case "hand": SystemSounds.Hand.Play(); break;
            default: SystemSounds.Asterisk.Play(); break;
        }
    }

    /// <summary>播放 wav（异步，不阻塞 UI）；失败返回 false 以便退回下一种声音。</summary>
    private static bool TryPlayWav(string path)
    {
        try
        {
            var player = new SoundPlayer(path);
            player.Load();
            player.Play();
            return true;
        }
        catch { return false; }
    }

    /// <summary>设置页列出可配置的来源：内置三个 + 声明了 notify.sound 的插件。</summary>
    public IReadOnlyList<(string Source, string Name, bool BuiltIn)> Sources()
    {
        var list = new List<(string, string, bool)>();
        foreach (var s in BuiltInSources)
        {
            var name = s switch { "qq" => "QQ", "wechat" => "微信", "mail" => "邮件", _ => s };
            list.Add((s, name, true));
        }

        try
        {
            foreach (var p in PluginRegistry.Plugins)
            {
                if (p.State != PluginState.Loaded) continue;
                if (!Array.Exists(p.Permissions,
                        x => string.Equals(x, PluginPermissions.NotifySound, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (list.Exists(x => string.Equals(x.Item1, p.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add((p.Id, string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name, false));
            }
        }
        catch { }

        return list;
    }
}
