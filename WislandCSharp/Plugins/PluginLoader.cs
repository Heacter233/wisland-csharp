using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Wisland.Services;

namespace Wisland.Plugins;

/// <summary>插件运行状态。</summary>
public enum PluginState
{
    /// <summary>已加载并正常运行。</summary>
    Loaded,
    /// <summary>加载失败（清单非法/版本不符/类型找不到/OnLoad 抛异常）。</summary>
    Failed,
    /// <summary>用户禁用。</summary>
    Disabled,
}

/// <summary>加载结果。</summary>
public sealed class PluginLoadResult
{
    public PluginManifest? Manifest { get; init; }
    public string Directory { get; init; } = "";
    public PluginState State { get; init; }
    public string Error { get; init; } = "";
    /// <summary>插件提供的设置面板（可能为 null）。</summary>
    public Func<System.Windows.FrameworkElement>? SettingsBuild { get; init; }
    public string SettingsTitle { get; init; } = "";
    public string SettingsSubtitle { get; init; } = "";
    /// <summary>插件注册的页面（可能为空）。</summary>
    public IReadOnlyList<PluginPageDescriptor> Pages { get; init; } = Array.Empty<PluginPageDescriptor>();
    internal IWislandPlugin? Instance { get; init; }
    internal PluginHostContext? Context { get; init; }
    internal PluginLoadContext? LoadContext { get; init; }
}

/// <summary>
/// 插件加载器。
///
/// 约定：
///   plugins/&lt;id&gt;/plugin.json      清单
///   plugins/&lt;id&gt;/&lt;assembly&gt;        程序集（可带同目录依赖）
///   data/plugins/&lt;id&gt;/             插件数据（config.json / plugin.log，宿主创建）
///
/// 隔离与容错：
///   - 每个插件一个可回收 AssemblyLoadContext，依赖只在插件目录里解析；
///   - 宿主程序集（Wisland）回落到默认上下文，保证契约类型是同一个；
///   - 清单非法、版本不符、类型缺失、OnLoad 抛异常 → 标记 Failed 并记录原因，
///     不影响宿主与其他插件；
///   - 卸载时先 OnUnload，再释放上下文并 Unload（尽力回收）。
/// </summary>
public static class PluginLoader
{
    private static readonly List<PluginLoadResult> _loaded = new();
    private static readonly object _lock = new();

    /// <summary>OnLoad 超时（秒）。超时后标记失败，但不会中断插件线程。</summary>
    public const int LoadTimeoutSeconds = 8;

    /// <summary>宿主版本（来自程序集）。</summary>
    public static string HostVersion =>
        (typeof(PluginLoader).Assembly.GetName().Version ?? new Version(0, 1, 0)).ToString(3);

    public static string PluginsRoot(string baseDir) => Path.Combine(baseDir, "plugins");
    public static string PluginDataRoot(string dataDir) => Path.Combine(dataDir, "plugins");

    public static IReadOnlyList<PluginLoadResult> Loaded
    {
        get { lock (_lock) return _loaded.ToArray(); }
    }

    /// <summary>
    /// 扫描并加载全部插件，结果写入 <see cref="PluginRegistry"/>。
    /// 必须在 UI 线程调用（需要 Dispatcher）。
    /// </summary>
    public static IReadOnlyList<PluginLoadResult> LoadAll(string baseDir, string dataDir, IConfigService config)
    {
        var root = PluginsRoot(baseDir);
        var dataRoot = PluginDataRoot(dataDir);
        var disabled = ReadDisabled(config);
        var ui = Dispatcher.CurrentDispatcher;
        var results = new List<PluginLoadResult>();

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(dataRoot);

        foreach (var dir in SafeGetDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            var folder = Path.GetFileName(dir);

            if (!File.Exists(manifestPath))
            {
                results.Add(new PluginLoadResult
                {
                    Directory = dir,
                    State = PluginState.Failed,
                    Error = "目录下没有 plugin.json，已忽略",
                });
                continue;
            }

            var manifest = PluginManifest.TryLoad(manifestPath, out var error);
            if (manifest == null)
            {
                results.Add(new PluginLoadResult
                {
                    Directory = dir,
                    State = PluginState.Failed,
                    Error = error,
                });
                continue;
            }

            if (!string.Equals(folder, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new PluginLoadResult
                {
                    Manifest = manifest,
                    Directory = dir,
                    State = PluginState.Failed,
                    Error = $"目录名 \"{folder}\" 与 id \"{manifest.Id}\" 不一致（必须以 id 命名目录）",
                });
                continue;
            }

            if (disabled.Contains(manifest.Id))
            {
                results.Add(new PluginLoadResult
                {
                    Manifest = manifest,
                    Directory = dir,
                    State = PluginState.Disabled,
                    Error = "已在设置中禁用",
                });
                continue;
            }

            if (!VersionOk(manifest.MinHostVersion))
            {
                results.Add(new PluginLoadResult
                {
                    Manifest = manifest,
                    Directory = dir,
                    State = PluginState.Failed,
                    Error = $"需要宿主 ≥ {manifest.MinHostVersion}，当前 {HostVersion}",
                });
                continue;
            }

            results.Add(LoadOne(manifest, dataRoot, ui));
        }

        // 注册到设置页
        foreach (var r in results) RegisterToRegistry(r);

        lock (_lock)
        {
            _loaded.Clear();
            _loaded.AddRange(results);
        }
        return results;
    }

    private static PluginLoadResult LoadOne(PluginManifest manifest, string dataRoot, Dispatcher ui)
    {
        PluginLoadContext? alc = null;
        PluginHostContext? ctx = null;
        try
        {
            var assemblyPath = Path.Combine(manifest.Directory, manifest.Assembly);
            alc = new PluginLoadContext(manifest.Directory);
            var asm = alc.LoadFromAssemblyPath(assemblyPath);

            var type = asm.GetType(manifest.Entry, throwOnError: false);
            if (type == null)
                return Fail(manifest, alc, $"找不到入口类型 {manifest.Entry}");

            if (!typeof(IWislandPlugin).IsAssignableFrom(type))
                return Fail(manifest, alc, $"入口类型未实现 IWislandPlugin：{manifest.Entry}");

            if (Activator.CreateInstance(type) is not IWislandPlugin instance)
                return Fail(manifest, alc, "无法实例化入口类型（需要公开的无参构造函数）");

            ctx = new PluginHostContext(manifest, ui, HostVersion, dataRoot);

            // OnLoad 放后台执行并限时，避免插件阻塞宿主启动
            var loadTask = Task.Run(() =>
            {
                instance.OnLoad(ctx);
                return true;
            });

            if (!loadTask.Wait(TimeSpan.FromSeconds(LoadTimeoutSeconds)))
            {
                ctx.Log.Error($"OnLoad 超过 {LoadTimeoutSeconds} 秒未返回，已判定加载失败（插件可能仍在后台运行）");
                return new PluginLoadResult
                {
                    Manifest = manifest,
                    Directory = manifest.Directory,
                    State = PluginState.Failed,
                    Error = $"OnLoad 超时（>{LoadTimeoutSeconds}s）",
                    Instance = instance,
                    Context = ctx,
                    LoadContext = alc,
                };
            }

            return new PluginLoadResult
            {
                Manifest = manifest,
                Directory = manifest.Directory,
                State = PluginState.Loaded,
                SettingsBuild = ctx.SettingsBuild,
                SettingsTitle = ctx.SettingsTitle,
                SettingsSubtitle = ctx.SettingsSubtitle,
                Pages = ctx.Pages,
                Instance = instance,
                Context = ctx,
                LoadContext = alc,
            };
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException agg ? agg.Flatten().InnerException ?? ex : ex;
            ctx?.Log.Error("插件加载失败", inner);
            return new PluginLoadResult
            {
                Manifest = manifest,
                Directory = manifest.Directory,
                State = PluginState.Failed,
                Error = inner.GetBaseException().Message,
                Context = ctx,
                LoadContext = alc,
            };
        }
    }

    private static PluginLoadResult Fail(PluginManifest manifest, PluginLoadContext alc, string error) => new()
    {
        Manifest = manifest,
        Directory = manifest.Directory,
        State = PluginState.Failed,
        Error = error,
        LoadContext = alc,
    };

    private static void RegisterToRegistry(PluginLoadResult r)
    {
        var m = r.Manifest;
        PluginRegistry.Register(new PluginInfo
        {
            Id = m?.Id ?? Path.GetFileName(r.Directory),
            Name = m?.Name ?? Path.GetFileName(r.Directory),
            Version = m?.Version ?? "-",
            Description = m?.Description ?? "",
            Author = m?.Author ?? "",
            State = r.State,
            Error = r.Error,
            Permissions = m?.Permissions?.ToArray() ?? Array.Empty<string>(),
            Settings = r.SettingsBuild == null
                ? null
                : new PluginSettingsSection
                {
                    Id = (m?.Id ?? "plugin") + ".settings",
                    Title = string.IsNullOrWhiteSpace(r.SettingsTitle) ? m?.Name ?? "插件设置" : r.SettingsTitle,
                    Subtitle = r.SettingsSubtitle,
                    Build = r.SettingsBuild,
                },
        });
    }

    /// <summary>卸载全部插件（宿主退出时调用）。</summary>
    public static void UnloadAll()
    {
        PluginLoadResult[] items;
        lock (_lock) items = _loaded.ToArray();
        lock (_lock) _loaded.Clear();

        foreach (var r in items)
        {
            try { r.Instance?.OnUnload(); } catch { }
            try { r.Context?.Dispose(); } catch { }
            try { r.LoadContext?.Unload(); } catch { }
        }
        PluginRegistry.Clear();
    }

    private static IEnumerable<string> SafeGetDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static HashSet<string> ReadDisabled(IConfigService config)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = config.Get("plugins.disabled", new List<object?>());
            if (raw is IEnumerable<object?> list)
            {
                foreach (var item in list)
                    if (item is string s && !string.IsNullOrWhiteSpace(s)) set.Add(s);
            }
        }
        catch { }
        return set;
    }

    /// <summary>宿主版本是否满足要求（如 "0.2.0"）。空字符串视为不校验。</summary>
    private static bool VersionOk(string minHostVersion)
    {
        if (string.IsNullOrWhiteSpace(minHostVersion)) return true;
        if (!Version.TryParse(Normalize(minHostVersion), out var min)) return true;
        if (!Version.TryParse(Normalize(HostVersion), out var host)) return true;
        return host >= min;
    }

    private static string Normalize(string v)
    {
        var parts = v.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => v,
        };
    }
}

/// <summary>
/// 插件专用加载上下文：依赖只在插件目录解析；
/// 请求宿主程序集时回落到默认上下文（保证 IWislandPlugin / PluginMessage 等类型同一份）。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private static readonly string HostAssemblyName = typeof(PluginLoadContext).Assembly.GetName().Name ?? "Wisland";

    public PluginLoadContext(string directory) : base($"wisland-plugin:{directory}", isCollectible: true)
    {
        _directory = directory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 宿主程序集：交给默认上下文，避免类型不匹配
        if (string.Equals(assemblyName.Name, HostAssemblyName, StringComparison.OrdinalIgnoreCase))
            return null;

        // 其余共享框架程序集（WPF/BCL）同样交给默认上下文
        var shared = assemblyName.Name ?? "";
        if (shared.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            shared.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            shared.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase) ||
            shared.Equals("PresentationCore", StringComparison.OrdinalIgnoreCase) ||
            shared.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) ||
            shared.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
            shared.Equals("netstandard", StringComparison.OrdinalIgnoreCase))
            return null;

        var candidate = Path.Combine(_directory, shared + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var candidate = Path.Combine(_directory, unmanagedDllName);
        if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);
        if (File.Exists(candidate + ".dll")) return LoadUnmanagedDllFromPath(candidate + ".dll");
        return IntPtr.Zero;
    }
}