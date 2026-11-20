using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>单个模型的下载状态（UI 只读取，不持有下载逻辑）。</summary>
public sealed class ModelDownloadState
{
    public string Model { get; init; } = "";

    /// <summary>是否正在下载。</summary>
    public bool Running { get; set; }

    /// <summary>0~1；服务器未给长度时为 0。</summary>
    public double Progress { get; set; }

    public long Bytes { get; set; }
    public long Total { get; set; }

    /// <summary>给用户看的当前状态文字（下载中 / 已取消 / 失败原因）。</summary>
    public string Message { get; set; } = "";

    /// <summary>失败原因（空 = 没失败）。</summary>
    public string Error { get; set; } = "";

    /// <summary>是不是被用户取消的（区别于失败）。</summary>
    public bool Canceled { get; set; }

    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// 模型下载管理器：进程级单例，下载任务与进度都放在这里。
///
/// 为什么不能放在页面里：页面（ModelsPage）随分段切换被重建/隐藏，
/// 页面内的 ProgressBar 与 CancellationTokenSource 都会丢，表现为
/// "切走再回来进度就没了、点下载也没反应"。状态搬到服务层后：
///   - 切页面不影响下载（任务还在跑）；
///   - 回到页面按服务状态重建进度条（进度不丢）；
///   - 取消与续传由服务统一管理（同一个模型不会并发下两份）。
///
/// 进度变化通过 <see cref="Changed"/> 通知（可能来自后台线程，订阅方自行 Dispatcher 切回）。
/// </summary>
public sealed class ModelManager
{
    private readonly IConfigService _cfg;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelDownloadState> _states = new();
    private readonly Dictionary<string, CancellationTokenSource> _cts = new();
    private readonly Dictionary<string, Task<bool>> _jobs = new();

    public ModelManager(IConfigService cfg) => _cfg = cfg;

    /// <summary>某个模型的状态变化（参数为模型名）。可能在后台线程触发。</summary>
    public event Action<string>? Changed;

    // ---------------- 路径 ----------------

    /// <summary>模型目录：live.models_dir（相对路径按 data/ 解析）。</summary>
    public string Dir
    {
        get
        {
            var dir = _cfg.Get("live.models_dir", "models");
            if (string.IsNullOrWhiteSpace(dir)) dir = "models";
            return Path.IsPathRooted(dir) ? dir : Path.Combine(_cfg.DataDir, dir);
        }
    }

    public string PathOf(string model) => Path.Combine(Dir, WhisperAsr.FileName(model));

    public string PartPathOf(string model) => PathOf(model) + ".part";

    /// <summary>模型文件是否已完整存在（>10MB 且文件存在）。</summary>
    public bool Exists(string model)
    {
        var path = PathOf(model);
        try { return File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024; }
        catch { return false; }
    }

    /// <summary>已缓存但未下完的字节数（0 = 没有缓存）。</summary>
    public long PartialBytes(string model)
    {
        try
        {
            var p = PartPathOf(model);
            return File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>上次下载时记录的总大小（缓存文件旁的 .total，用于续传时先画出进度）。</summary>
    public long TotalHintBytes(string model)
    {
        try
        {
            var p = PartPathOf(model) + ".total";
            if (!File.Exists(p)) return 0;
            return long.TryParse(File.ReadAllText(p).Trim(), out var v) ? v : 0;
        }
        catch { return 0; }
    }

    public long SizeBytes(string model)
    {
        try { var p = PathOf(model); return File.Exists(p) ? new FileInfo(p).Length : 0; }
        catch { return 0; }
    }

    /// <summary>删除模型文件（不动 .part）。</summary>
    public void Delete(string model)
    {
        try { if (File.Exists(PathOf(model))) File.Delete(PathOf(model)); }
        catch { }
        Raise(model);
    }

    /// <summary>丢弃未完成缓存。</summary>
    public void ClearPartial(string model)
    {
        try { if (File.Exists(PartPathOf(model))) File.Delete(PartPathOf(model)); } catch { }
        try { if (File.Exists(PartPathOf(model) + ".total")) File.Delete(PartPathOf(model) + ".total"); } catch { }
        Raise(model);
    }

    // ---------------- 状态 ----------------

    public ModelDownloadState StateOf(string model)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(model, out var st))
            {
                st = new ModelDownloadState { Model = model, Message = "" };
                _states[model] = st;
            }
            return st;
        }
    }

    public bool IsDownloading(string model)
    {
        lock (_gate) return _jobs.ContainsKey(model);
    }

    /// <summary>是否有任一模型正在下载（用于实时字幕页的"取消下载"）。</summary>
    public bool AnyDownloading
    {
        get { lock (_gate) return _jobs.Count > 0; }
    }

    /// <summary>取消下载（保留 .part，可续传）。</summary>
    public void Cancel(string model)
    {
        CancellationTokenSource? cts;
        lock (_gate) _cts.TryGetValue(model, out cts);
        try { cts?.Cancel(); } catch { }
    }

    public void CancelAll()
    {
        List<CancellationTokenSource> list;
        lock (_gate) list = new List<CancellationTokenSource>(_cts.Values);
        foreach (var c in list) { try { c.Cancel(); } catch { } }
    }

    /// <summary>开始下载（同一模型已在下载则复用同一个任务）。</summary>
    public Task<bool> DownloadAsync(string model)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(model, out var running)) return running;

            var st = StateOf(model);
            st.Running = true;
            st.Error = "";
            st.Canceled = false;

            // 续传：先用缓存文件 + .total 画出"已完成"的那一段，避免连接期间显示 0%
            var cached = PartialBytes(model);
            st.Bytes = cached;
            st.Total = TotalHintBytes(model);
            st.Progress = st.Total > 0 ? Math.Min(1.0, (double)cached / st.Total) : 0;
            st.Message = cached > 0 ? "正在续传…" : "准备下载…";

            var cts = new CancellationTokenSource();
            _cts[model] = cts;

            var job = RunAsync(model, st, cts);
            _jobs[model] = job;
            Raise(model);          // 注意：在 lock 内 Raise 会让订阅方（UI 线程）读到一致状态
            return job;
        }
    }

    /// <summary>确保模型可用：已存在直接成功，否则下载。返回 false 表示取消/失败。</summary>
    public async Task<bool> EnsureAsync(string model, Action<DownloadProgress>? progress = null)
    {
        if (Exists(model)) return true;

        // 把外部回调挂到状态变化上（用于实时字幕页显示了进度）
        void OnChanged(string name)
        {
            if (!string.Equals(name, model, StringComparison.OrdinalIgnoreCase)) return;
            var st = StateOf(model);
            progress?.Invoke(new DownloadProgress(st.Progress, st.Bytes, st.Total, st.Message));
        }

        Changed += OnChanged;
        try { return await DownloadAsync(model); }
        finally { Changed -= OnChanged; }
    }

    private async Task<bool> RunAsync(string model, ModelDownloadState st, CancellationTokenSource cts)
    {
        await Task.Yield();          // 让出 lock，_jobs 先登记好

        try
        {
            var asr = new WhisperAsr(Dir);
            await asr.DownloadAsync(model, p =>
            {
                st.Progress = p.Ratio;
                st.Bytes = p.Bytes;
                st.Total = p.Total;
                st.Message = p.Text;
                Raise(model);
            }, cts.Token);

            st.Running = false;
            st.Progress = 1;
            st.Bytes = SizeBytes(model);
            st.Total = st.Bytes;
            st.Message = "已下载完成";
            st.Error = "";
            st.Canceled = false;
            st.FinishedAt = DateTime.Now;
            return true;
        }
        catch (OperationCanceledException)
        {
            st.Running = false;
            st.Canceled = true;
            st.Error = "";
            var cached = PartialBytes(model);
            st.Bytes = cached;
            st.Message = cached > 0
                ? $"已取消（已缓存 {cached / 1048576.0:0.0} MB，点「继续」可续传）"
                : "已取消下载";
            return false;
        }
        catch (Exception ex)
        {
            st.Running = false;
            st.Error = ex.Message;
            var cached = PartialBytes(model);
            st.Bytes = cached;
            st.Message = "下载失败：" + FirstLine(ex.Message) +
                         (cached > 0 ? $"（已缓存 {cached / 1048576.0:0.0} MB，可重试续传）" : "");
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _jobs.Remove(model);
                _cts.Remove(model);
            }
            st.Running = false;
            try { cts.Dispose(); } catch { }
            Raise(model);
        }
    }

    private static string FirstLine(string text)
    {
        var i = text.IndexOfAny(new[] { '\r', '\n' });
        return i > 0 ? text[..i] : text;
    }

    private void Raise(string model) => Changed?.Invoke(model);
}
