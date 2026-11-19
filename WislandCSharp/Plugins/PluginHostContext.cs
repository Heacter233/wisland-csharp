using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wisland.Services;

namespace Wisland.Plugins;

/// <summary>
/// 单个插件的上下文实现：配置存储、日志、权限守门、宿主托管的定时器。
/// 一个插件一个实例，卸载时全部资源随之释放。
/// </summary>
internal sealed class PluginHostContext : IPluginContext, IDisposable
{
    private readonly List<IDisposable> _timers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Action> _settingsBuilders = new();
    private readonly object _lock = new();

    public PluginManifest Manifest { get; }
    public IPluginConfig Config { get; }
    public IPluginLogger Log { get; }
    public string DataDirectory { get; }
    public string PluginDirectory { get; }
    public Dispatcher Ui { get; }
    public CancellationToken ShutdownToken => _shutdown.Token;
    public string HostVersion { get; }

    /// <summary>插件通过 RegisterSettingsSection 注册的面板（可能为 null）。</summary>
    public Func<FrameworkElement>? SettingsBuild { get; private set; }
    public string SettingsTitle { get; private set; } = "";
    public string SettingsSubtitle { get; private set; } = "";

    public PluginHostContext(PluginManifest manifest, Dispatcher ui, string hostVersion, string dataRoot)
    {
        Manifest = manifest;
        Ui = ui;
        HostVersion = hostVersion;
        PluginDirectory = manifest.Directory;
        DataDirectory = Path.Combine(dataRoot, manifest.Id);
        System.IO.Directory.CreateDirectory(DataDirectory);
        Config = new PluginConfigStore(Path.Combine(DataDirectory, "config.json"));
        Log = new PluginLogger(Path.Combine(DataDirectory, "plugin.log"), manifest.Id);
    }

    public bool Has(string permission) => Manifest.Has(permission);

    /// <summary>权限守门：未声明则拒绝并记日志。</summary>
    private bool Ensure(string permission, string action)
    {
        if (Manifest.Has(permission)) return true;
        Log.Warn($"权限不足：{action} 需要 \"{permission}\"，请在 plugin.json 的 permissions 中声明");
        return false;
    }

    public void PushMessage(PluginMessage message)
    {
        if (message == null) return;
        if (!Ensure(PluginPermissions.MessagesPush, "推送消息")) return;

        // 插件未声明 ui.compact 时忽略自定义动画，避免"悄悄画东西"
        if (!Manifest.Has(PluginPermissions.CompactDraw))
            message = StripDraw(message);

        if (!Manifest.Has(PluginPermissions.NotifyImage))
            message = StripImage(message);

        ServiceHub.Inbox.Push(message);
    }

    private static PluginMessage StripDraw(PluginMessage m) => new()
    {
        Source = m.Source, SourceName = m.SourceName, Title = m.Title, Body = m.Body,
        Time = m.Time, Level = m.Level, IconGlyph = m.IconGlyph, IconColor = m.IconColor,
        Kind = m.Kind, Uri = m.Uri, Action = m.Action,
    };

    private static PluginMessage StripImage(PluginMessage m) => new()
    {
        Source = m.Source, SourceName = m.SourceName, Title = m.Title, Body = m.Body,
        Time = m.Time, Level = m.Level, IconGlyph = m.IconGlyph, IconColor = m.IconColor,
        Kind = m.Kind, Uri = m.Uri, Action = m.Action,
        NotifyDraw = m.NotifyDraw,
    };

    public void RequestExpand(string pageKey = "") => ServiceHub.RequestExpand?.Invoke(pageKey);

    public void RequestCollapse() => ServiceHub.RequestCollapse?.Invoke();

    public void RequestRetargetHeight() => ServiceHub.RequestRetargetHeight?.Invoke();

    public IDisposable RegisterTimer(TimeSpan interval, Action tick, bool runOnUi = true)
    {
        if (tick == null) return new NoopDisposable();

        // 最小 100ms：防止插件用高频定时器拖垮宿主
        if (interval < TimeSpan.FromMilliseconds(100)) interval = TimeSpan.FromMilliseconds(100);

        var timer = new DispatcherTimer(DispatcherPriority.Background, Ui) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            if (_shutdown.IsCancellationRequested) return;
            try
            {
                if (runOnUi || Ui.CheckAccess()) tick();
                else Ui.BeginInvoke(tick, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Log.Error("定时器回调抛出异常", ex);
            }
        };
        timer.Start();

        var handle = new TimerHandle(timer);
        lock (_lock) _timers.Add(handle);
        return handle;
    }

    public void RegisterSettingsSection(string title, string subtitle, Func<FrameworkElement> build)
    {
        if (build == null) return;
        if (!Ensure(PluginPermissions.SettingsPanel, "注册设置分组")) return;

        SettingsTitle = string.IsNullOrWhiteSpace(title) ? Manifest.Name : title;
        SettingsSubtitle = subtitle ?? "";
        SettingsBuild = build;
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch { }
        lock (_lock)
        {
            foreach (var t in _timers)
            {
                try { t.Dispose(); } catch { }
            }
            _timers.Clear();
        }
        (Config as IDisposable)?.Dispose();
        _shutdown.Dispose();
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    private sealed class TimerHandle : IDisposable
    {
        private DispatcherTimer? _timer;
        public TimerHandle(DispatcherTimer timer) => _timer = timer;
        public void Dispose()
        {
            if (_timer == null) return;
            try { _timer.Stop(); } catch { }
            _timer = null;
        }
    }
}

/// <summary>插件独立配置存储（data/plugins/&lt;id&gt;/config.json）。</summary>
internal sealed class PluginConfigStore : IPluginConfig, IDisposable
{
    private readonly string _path;
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event EventHandler? Changed;

    public PluginConfigStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict == null) return;
            lock (_lock)
            {
                foreach (var kv in dict)
                    _values[kv.Key] = Convert(kv.Value);
            }
        }
        catch { }
    }

    private static object? Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.GetRawText(),
    };

    public T Get<T>(string key, T fallback = default!)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var v) || v == null) return fallback;
            try
            {
                if (v is T t) return t;
                var target = typeof(T);
                if (target == typeof(string)) return (T)(object)v.ToString()!;
                return (T)System.Convert.ChangeType(v, target);
            }
            catch { return fallback; }
        }
    }

    public void Set<T>(string key, T value, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_lock) _values[key] = value;
        if (save) Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            Dictionary<string, object?> copy;
            lock (_lock) copy = new Dictionary<string, object?>(_values);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json, Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose() => Save();
}

/// <summary>插件日志：写入 data/plugins/&lt;id&gt;/plugin.log，超过 512KB 自动轮转一次。</summary>
internal sealed class PluginLogger : IPluginLogger
{
    private const long MaxBytes = 512 * 1024;
    private readonly string _path;
    private readonly string _id;
    private readonly object _lock = new();

    public PluginLogger(string path, string id)
    {
        _path = path;
        _id = id;
    }

    public void Debug(string message) => Write("DBG", message, null);
    public void Info(string message) => Write("INF", message, null);
    public void Warn(string message) => Write("WRN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERR", message, exception);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_path)!;
                System.IO.Directory.CreateDirectory(dir);

                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = _path + ".1";
                    try { File.Delete(old); } catch { }
                    try { File.Move(_path, old); } catch { }
                }

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(level).Append("] ")
                  .Append('[').Append(_id).Append("] ").AppendLine(message);
                if (ex != null) sb.AppendLine(ex.ToString());

                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { }
    }
}