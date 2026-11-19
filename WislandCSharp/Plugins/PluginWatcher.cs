using System;
using System.IO;
using System.Windows.Threading;

namespace Wisland.Plugins;

/// <summary>
/// 插件目录监视器：在 plugins/ 下的文件发生变化后，防抖触发一次热重载。
///
/// 关注的变化：
///   - 新增/删除插件目录
///   - 任意 plugin.json 的改动
///   - 任意 .dll / .pdb 的改动（编译输出被覆盖）
///   - 目录重命名
///
/// 防抖：编辑器保存一次会连续产生多个事件（尤其是多文件复制），
/// 所以等 <see cref="Debounce"/> 内没有新事件再回调，避免重载十几遍。
/// 回调通过 <see cref="Dispatcher"/> 回到 UI 线程执行。
/// </summary>
public sealed class PluginWatcher : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(700);

    private readonly FileSystemWatcher _fsw;
    private readonly DispatcherTimer _timer;
    private readonly Action _onChanged;
    private bool _disposed;
    private bool _suppressed;

    /// <summary>最近一次触发原因（调试/日志用）。</summary>
    public string LastReason { get; private set; } = "";

    public PluginWatcher(string pluginsRoot, Action onChanged, Dispatcher ui)
    {
        _onChanged = onChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = Debounce };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (_disposed || _suppressed) return;
            try { _onChanged(); } catch { }
        };

        Directory.CreateDirectory(pluginsRoot);

        _fsw = new FileSystemWatcher(pluginsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
            EnableRaisingEvents = true,
            InternalBufferSize = 64 * 1024,
        };
        _fsw.Changed += OnEvent;
        _fsw.Created += OnEvent;
        _fsw.Deleted += OnEvent;
        _fsw.Renamed += OnRenamed;
        _fsw.Error += (_, _) => { /* 缓冲溢出：忽略这一轮，下一次变更会重新触发 */ };
    }

    private bool Interesting(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)) return true;
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed || !Interesting(e.FullPath)) return;
        LastReason = $"{e.ChangeType}: {e.Name}";
        Restart();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_disposed) return;
        if (!Interesting(e.FullPath) && !Interesting(e.OldFullPath)) return;
        LastReason = $"Rename: {e.OldName} → {e.Name}";
        Restart();
    }

    /// <summary>重新计时（防抖）。</summary>
    private void Restart()
    {
        try
        {
            _timer.Stop();
            _timer.Start();
        }
        catch { }
    }

    /// <summary>宿主自己操作插件目录时临时屏蔽（例如安装器写入）。</summary>
    public IDisposable Suppress()
    {
        _suppressed = true;
        _timer.Stop();
        return new Resumer(this);
    }

    private sealed class Resumer : IDisposable
    {
        private PluginWatcher? _owner;
        public Resumer(PluginWatcher owner) => _owner = owner;
        public void Dispose()
        {
            if (_owner == null) return;
            _owner._suppressed = false;
            _owner = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fsw.EnableRaisingEvents = false; } catch { }
        try { _fsw.Dispose(); } catch { }
        try { _timer.Stop(); } catch { }
    }
}
