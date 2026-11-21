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
    ///
    /// 必须在后台线程 await（不要同步 Wait）：OnLoad 是插件自己的代码，可能耗时或阻塞，
    /// 早期版本在 UI 线程 <c>loadTask.Wait(8s)</c>，插件若在 OnLoad 里 Ui.Invoke(...) 就会与宿主互等，
    /// 表现为启动卡满 8 秒后判定插件失败。
    /// </summary>
    /// <param name="ui">UI Dispatcher（用于插件定时器/回调，必须由调用方从 UI 线程传入）。</param>
    public static async Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(
        string baseDir, string dataDir, IConfigService config, Dispatcher ui)
    {
        var root = PluginsRoot(baseDir);
        var dataRoot = PluginDataRoot(dataDir);
        var shadowRoot = Path.Combine(dataRoot, ".shadow");

        // 上一轮进程遗留的影子目录：尽量清掉（被占用就跳过）
        PluginShadow.CleanupAll(shadowRoot);
        var disabled = ReadDisabled(config);
        var results = new List<PluginLoadResult>();

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(dataRoot);

        foreach (var dir in SafeGetDirectories(root))
            results.Add(await LoadDirectoryAsync(dir, disabled, ui, dataRoot, shadowRoot).ConfigureAwait(false));

        // 注册到设置页
        foreach (var r in results) RegisterToRegistry(r);

        lock (_lock)
        {
            _loaded.Clear();
            _loaded.AddRange(results);
        }
        return results;
    }

    /// <summary>
    /// 只重载指定的一个插件（其它插件保持运行）。
    /// 用于设置页每个插件卡片上的「重载」按钮。
    ///
    /// 会先卸载该插件、清掉它的注册表条目，再重新扫描/加载它自己的目录。
    /// 目录被删掉时返回 null（调用方据此重建页面列表即可）。
    /// </summary>
    public static async Task<PluginLoadResult?> ReloadOneAsync(
        string id, string baseDir, string dataDir, IConfigService config, Dispatcher ui)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var root = PluginsRoot(baseDir);
        var dataRoot = PluginDataRoot(dataDir);
        var shadowRoot = Path.Combine(dataRoot, ".shadow");

        PluginLoadResult? old = null;
        lock (_lock)
        {
            old = _loaded.Find(r =>
                string.Equals(r.Manifest?.Id ?? Path.GetFileName(r.Directory), id, StringComparison.OrdinalIgnoreCase));
            if (old != null) _loaded.Remove(old);
        }

        // 卸载旧实例（OnUnload → 释放上下文 → ALC.Unload）
        UnloadOne(old);

        // 注册表里也要清掉，等重新加载后重新登记
        PluginRegistry.Unregister(id);

        var dir = old?.Directory ?? Path.Combine(root, id);
        if (!Directory.Exists(dir))
        {
            AppDebug($"插件目录已不存在，视为移除：{dir}");
            return null;
        }

        var result = await LoadDirectoryAsync(dir, ReadDisabled(config), ui, dataRoot, shadowRoot)
            .ConfigureAwait(false);

        lock (_lock) _loaded.Add(result);
        RegisterToRegistry(result);
        return result;
    }

    private static void UnloadOne(PluginLoadResult? r)
    {
        if (r == null) return;
        try { r.Instance?.OnUnload(); } catch { }
        try { r.Context?.Dispose(); } catch { }
        try { r.LoadContext?.Unload(); } catch { }
    }

    /// <summary>按 id 查已加载结果。</summary>
    public static PluginLoadResult? Find(string id)
    {
        lock (_lock)
        {
            return _loaded.Find(r =>
                string.Equals(r.Manifest?.Id ?? Path.GetFileName(r.Directory), id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AppDebug(string note)
    {
        try { Wisland.App.DebugNote(note); } catch { }
    }

    /// <summary>加载一个插件目录（含清单校验）。失败时返回 Failed 结果而不是抛异常。</summary>
    private static async Task<PluginLoadResult> LoadDirectoryAsync(
        string dir, HashSet<string> disabled, Dispatcher ui, string dataRoot, string shadowRoot)
    {
        var manifestPath = Path.Combine(dir, "plugin.json");
        var folder = Path.GetFileName(dir);

        if (!File.Exists(manifestPath))
            return new PluginLoadResult
            {
                Directory = dir,
                State = PluginState.Failed,
                Error = "目录下没有 plugin.json，已忽略",
            };

        var manifest = PluginManifest.TryLoad(manifestPath, out var error);
        if (manifest == null)
            return new PluginLoadResult
            {
                Directory = dir,
                State = PluginState.Failed,
                Error = error,
            };

        if (!string.Equals(folder, manifest.Id, StringComparison.OrdinalIgnoreCase))
            return new PluginLoadResult
            {
                Manifest = manifest,
                Directory = dir,
                State = PluginState.Failed,
                Error = $"目录名 \"{folder}\" 与 id \"{manifest.Id}\" 不一致（必须以 id 命名目录）",
            };

        if (disabled.Contains(manifest.Id))
            return new PluginLoadResult
            {
                Manifest = manifest,
                Directory = dir,
                State = PluginState.Disabled,
                Error = "已在设置中禁用",
            };

        if (!VersionOk(manifest.MinHostVersion))
            return new PluginLoadResult
            {
                Manifest = manifest,
                Directory = dir,
                State = PluginState.Failed,
                Error = $"需要宿主 ≥ {manifest.MinHostVersion}，当前 {HostVersion}",
            };

        return await LoadOneAsync(manifest, dataRoot, shadowRoot, ui).ConfigureAwait(false);
    }

    /// <summary>
    /// 卸载全部插件后重新扫描加载（热重载）。
    /// 因为加载走影子副本，源目录的 dll 从未被锁定，用户可以直接覆盖文件后重载。
    /// </summary>
    public static async Task<IReadOnlyList<PluginLoadResult>> ReloadAsync(
        string baseDir, string dataDir, IConfigService config, Dispatcher ui)
    {
        UnloadAll();

        // 帮一把回收：让刚 Unload 的 ALC 尽早释放（放到线程池做全量 GC，
        // 避免热重载时在 UI 线程上停顿——宿主此前是在 UI 线程两次 GC.Collect）
        await Task.Run(() =>
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }
        }).ConfigureAwait(false);

        return await LoadAllAsync(baseDir, dataDir, config, ui).ConfigureAwait(false);
    }

    private static async Task<PluginLoadResult> LoadOneAsync(
        PluginManifest manifest, string dataRoot, string shadowRoot, Dispatcher ui)
    {
        PluginLoadContext? alc = null;
        PluginHostContext? ctx = null;
        try
        {
            // 从影子副本加载：源目录不被锁定，才能支持热重载
            var loadDir = PluginShadow.Prepare(manifest, shadowRoot, out var shadowError);
            if (loadDir == null) return Fail(manifest, null, shadowError);

            var assemblyPath = Path.Combine(loadDir, manifest.Assembly);
            alc = new PluginLoadContext(loadDir);
            var asm = alc.LoadFromAssemblyPath(assemblyPath);

            var type = asm.GetType(manifest.Entry, throwOnError: false);
            if (type == null)
                return Fail(manifest, alc, $"找不到入口类型 {manifest.Entry}");

            if (!typeof(IWislandPlugin).IsAssignableFrom(type))
                return Fail(manifest, alc, $"入口类型未实现 IWislandPlugin：{manifest.Entry}");

            if (Activator.CreateInstance(type) is not IWislandPlugin instance)
                return Fail(manifest, alc, "无法实例化入口类型（需要公开的无参构造函数）");

            ctx = new PluginHostContext(manifest, ui, HostVersion, dataRoot);

            // OnLoad 限时执行（不阻塞 UI 线程：这里是 await 而不是 Wait）
            var loadTask = Task.Run(() =>
            {
                instance.OnLoad(ctx);
                return true;
            });

            if (await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(LoadTimeoutSeconds)))
                    .ConfigureAwait(false) != loadTask)
            {
                // 超时：插件线程停不下来（.NET 无法强杀线程），但必须让它失去能力——
                // 撤销上下文（停掉定时器 + 后续 ctx 调用一律拒绝），否则就是"设置页显示失败、
                // 消息页却还在收它推送"的僵尸插件。
                ctx.Log.Error($"OnLoad 超过 {LoadTimeoutSeconds} 秒未返回，已判定加载失败并撤销其上下文");
                try { ctx.Dispose(); } catch { }

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

            // 让 OnLoad 里的异常浮上来（而不是静默变成"加载成功"）
            try { await loadTask.ConfigureAwait(false); }
            catch (Exception loadEx)
            {
                var innerLoad = loadEx is AggregateException aggLoad ? aggLoad.Flatten().InnerException ?? loadEx : loadEx;
                ctx.Log.Error("OnLoad 抛出异常", innerLoad);
                return new PluginLoadResult
                {
                    Manifest = manifest,
                    Directory = manifest.Directory,
                    State = PluginState.Failed,
                    Error = "OnLoad 失败：" + innerLoad.GetBaseException().Message,
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

    private static PluginLoadResult Fail(PluginManifest manifest, PluginLoadContext? alc, string error) => new()
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

        // 登记/撤销 Inbox 允许的来源 id：插件推送消息时 Source 必须是自己的 id，
        // 否则 MessageInbox.Push 会直接丢弃（防止冒充 qq/wechat/mail 或借别的插件 id）。
        try
        {
            var srcId = m?.Id ?? "";
            if (srcId.Length > 0)
            {
                if (r.State == PluginState.Loaded) ServiceHub.Inbox.RegisterPluginSource(srcId);
                else ServiceHub.Inbox.UnregisterPluginSource(srcId);
            }
        }
        catch { }

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
            // 撤销 Inbox 来源登记：卸载后同名来源的消息不应再被接受
            try
            {
                var id = r.Manifest?.Id ?? "";
                if (id.Length > 0) ServiceHub.Inbox.UnregisterPluginSource(id);
            }
            catch { }
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

        // fail-closed：清单里写了要求却解析不出来（"latest" / "0.2.0-beta" 这类），
        // 宁可拒绝加载也不要"放行一个自称需要新版本的插件"。
        if (!Version.TryParse(Normalize(minHostVersion), out var min))
        {
            AppDebug($"插件 minHostVersion 无法解析：'{minHostVersion}'，按不满足处理");
            return false;
        }
        if (!Version.TryParse(Normalize(HostVersion), out var host))
        {
            AppDebug($"宿主版本无法解析：'{HostVersion}'，按不满足处理");
            return false;
        }
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