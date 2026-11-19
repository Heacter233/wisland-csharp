using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wisland.Services;

/// <summary>
/// 配置服务接口：读写 config.json，支持点号路径访问。
/// 对应 Python 配置管理 cfg.get / cfg.set / Config.changed 信号。
/// </summary>
public interface IConfigService
{
    T? Get<T>(string key, T? defaultValue = default);
    void Set<T>(string key, T value, bool save = true);
    void Save();
    void Reload();
    string ConfigPath { get; }
    string DataDir { get; }
    event EventHandler? Changed;

    /// <summary>读取启动器列表（config.json → launcher[]）。</summary>
    System.Collections.Generic.List<LauncherItem> GetLauncher();

    /// <summary>保存启动栏列表（写入 config.json → launcher[]）。</summary>
    void SaveLauncher(System.Collections.Generic.List<LauncherItem> items);
}

/// <summary>
/// 配置服务实现：
/// - 内置与 Python 版一致的 DEFAULTS；
/// - 首次运行时自动从旧版（Python 项目根 / %APPDATA%\wisland）迁移 config.json；
/// - 深合并：文件值覆盖默认值，未写的键回落到默认值。
/// </summary>
public class ConfigService : IConfigService
{
    private static readonly Dictionary<string, object?> Defaults = new()
    {
        ["island"] = new Dictionary<string, object?>
        {
            ["edge"] = "top", ["pos"] = 0.5, ["screen"] = 0, ["theme"] = "auto",
            ["hide_fullscreen"] = true, ["hover_expand_ms"] = 220, ["collapse_delay_ms"] = 450,
            ["blur_fps"] = 8, ["default_tab"] = "home", ["expand_mode"] = "hover",
            ["compact_view"] = "clock", ["auto_cycle_s"] = 30, ["passthrough"] = false,
            ["autostart"] = false,
        },
        ["hotkeys"] = new Dictionary<string, object?>
        {
            ["toggle"] = "Ctrl+Alt+W", ["translate"] = "Ctrl+Alt+I", ["clipboard"] = "Ctrl+Alt+C",
        },
        ["media"] = new Dictionary<string, object?>
        {
            ["auto_show"] = true, ["lyrics"] = true, ["netease_log"] = true,
            ["priority"] = new List<object?> { "cloudmusic", "bilibili", "chrome", "zen", "msedge", "firefox" },
        },
        ["weather"] = new Dictionary<string, object?>
        {
            ["api_key"] = "", ["city"] = "上海", ["lat"] = null, ["lon"] = null,
            ["units"] = "metric", ["lang"] = "zh_cn", ["interval_min"] = 15,
            ["cities"] = new List<object?>(),
        },
        ["translate"] = new Dictionary<string, object?>
        {
            ["api_key"] = "", ["base_url"] = "https://api.deepseek.com",
            ["model"] = "deepseek-flash", ["import_from"] = "",
        },
        ["clipboard"] = new Dictionary<string, object?> { ["max"] = 10 },
        ["notify"] = new Dictionary<string, object?>
        {
            ["sound"] = new Dictionary<string, object?>
            {
                // 默认关闭：内置来源（QQ/微信/邮件）与插件来源都各自有开关
                ["enabled"] = false,
                ["kind"] = "builtin",
                ["file"] = "",
                ["cooldown_ms"] = 3000,
                ["source"] = new Dictionary<string, object?>
                {
                    ["qq"] = false, ["wechat"] = false, ["mail"] = false,
                },
            },
        },
        ["live"] = new Dictionary<string, object?>
        {
            ["models_dir"] = "models",
            ["audio"] = new Dictionary<string, object?> { ["source"] = "loopback", ["device"] = "", ["block_ms"] = 80 },
        },
    };

    private readonly string _configPath;
    private readonly string _dataDir;
    private readonly object _lock = new();
    private Dictionary<string, object?> _values = new();

    public string ConfigPath => _configPath;
    public string DataDir => _dataDir;
    public event EventHandler? Changed;

    public ConfigService(string? configPath = null, string? dataDir = null)
    {
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        _dataDir = dataDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDir);

        // 首次运行：从旧版 Python 项目（向上查找）或 %APPDATA%\wisland 迁移配置
        if (!File.Exists(_configPath))
        {
            var source = FindLegacyConfig();
            if (source != null)
            {
                try { File.Copy(source, _configPath); } catch { }
            }
        }
        LoadFromDisk();
    }

    /// <summary>向上最多 5 级查找旧版 wisland 的 config.json（bin/Debug/net8.0-windows → 项目根）。</summary>
    private static string? FindLegacyConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent!)
        {
            try
            {
                var candidate = Path.Combine(dir.FullName, "config.json");
                if (File.Exists(candidate) && LooksLikeWislandConfig(candidate)) return candidate;
            }
            catch { }
        }
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wisland", "config.json");
        return File.Exists(appData) ? appData : null;
    }

    private static bool LooksLikeWislandConfig(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   (doc.RootElement.TryGetProperty("island", out _) ||
                    doc.RootElement.TryGetProperty("weather", out _) ||
                    doc.RootElement.TryGetProperty("media", out _));
        }
        catch { return false; }
    }

    private void LoadFromDisk()
    {
        var merged = DeepClone(Defaults);
        if (File.Exists(_configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
                MergeInto(merged, ToObject(doc.RootElement) as Dictionary<string, object?> ?? new());
            }
            catch { /* 配置损坏则只使用默认值 */ }
        }
        lock (_lock) _values = merged;
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        lock (_lock)
        {
            object? cur = _values;
            foreach (var part in key.Split('.'))
            {
                if (cur is not Dictionary<string, object?> d || !d.TryGetValue(part, out var next))
                    return defaultValue;
                cur = next;
            }
            if (cur is null) return defaultValue;
            if (cur is T t) return t;
            try
            {
                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T?)Convert.ChangeType(cur, targetType);
            }
            catch { return defaultValue; }
        }
    }

    public void Set<T>(string key, T value, bool save = true)
    {
        if (value is null) return;
        lock (_lock)
        {
            var parts = key.Split('.');
            var node = _values;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!node.TryGetValue(parts[i], out var child) || child is not Dictionary<string, object?> sub)
                {
                    sub = new Dictionary<string, object?>();
                    node[parts[i]] = sub;
                }
                node = sub;
            }
            node[parts[^1]] = value;
        }
        if (save) Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            Dictionary<string, object?> snapshot;
            lock (_lock) snapshot = DeepClone(_values);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    public void Reload()
    {
        LoadFromDisk();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>读取启动器列表（config.json → launcher[]）。</summary>
    public void SaveLauncher(List<LauncherItem> items)
    {
        lock (_lock)
        {
            var arr = new List<object?>();
            foreach (var it in items)
            {
                var d = new Dictionary<string, object?>
                {
                    ["name"] = it.Name,
                    ["path"] = it.Path,
                };
                if (!string.IsNullOrEmpty(it.Glyph)) d["glyph"] = it.Glyph;
                if (!string.IsNullOrEmpty(it.Exe)) d["exe"] = it.Exe;
                if (it.Pinned) d["pinned"] = true;
                if (!string.IsNullOrEmpty(it.Icon)) d["icon"] = it.Icon;
                if (it.IsFolder) d["folder"] = true;
                if (!string.IsNullOrEmpty(it.Args)) d["args"] = it.Args;
                arr.Add(d);
            }
            _values["launcher"] = arr;
        }
        Save();
    }

    public List<LauncherItem> GetLauncher()
    {
        var list = new List<LauncherItem>();
        lock (_lock)
        {
            if (!_values.TryGetValue("launcher", out var node) || node is not List<object?> arr) return list;
            foreach (var it in arr)
            {
                if (it is not Dictionary<string, object?> d) continue;
                list.Add(new LauncherItem
                {
                    Name = d.TryGetValue("name", out var n) ? n as string ?? "" : "",
                    Path = d.TryGetValue("path", out var p) ? p as string ?? "" : "",
                    Glyph = d.TryGetValue("glyph", out var g) ? g as string ?? "" : "",
                    Exe = d.TryGetValue("exe", out var e) ? e as string ?? "" : "",
            Pinned = d.TryGetValue("pinned", out var pin) && pin is bool pb && pb,
            Icon = d.TryGetValue("icon", out var ic) ? ic as string ?? "" : "",
            IsFolder = d.TryGetValue("folder", out var fo) && fo is bool fb && fb,
            Args = d.TryGetValue("args", out var ar) ? ar as string ?? "" : "",
                });
            }
        }
        return list;
    }

    private static object? ToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => ToDict(el),
        JsonValueKind.Array => System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(el.EnumerateArray(), ToObject)),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> ToDict(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject()) d[p.Name] = ToObject(p.Value);
        return d;
    }

    private static void MergeInto(Dictionary<string, object?> target, Dictionary<string, object?> source)
    {
        foreach (var kv in source)
        {
            if (kv.Value is Dictionary<string, object?> srcSub &&
                target.TryGetValue(kv.Key, out var existing) &&
                existing is Dictionary<string, object?> tgtSub)
            {
                MergeInto(tgtSub, srcSub);
            }
            else
            {
                target[kv.Key] = kv.Value;
            }
        }
    }

    private static Dictionary<string, object?> DeepClone(Dictionary<string, object?> src)
    {
        var dst = new Dictionary<string, object?>();
        foreach (var kv in src)
        {
            if (kv.Value is Dictionary<string, object?> sub) dst[kv.Key] = DeepClone(sub);
            else if (kv.Value is List<object?> list) dst[kv.Key] = new List<object?>(list);
            else dst[kv.Key] = kv.Value;
        }
        return dst;
    }
}
