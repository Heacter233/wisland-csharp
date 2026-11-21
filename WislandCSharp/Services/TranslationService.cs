using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>一条翻译历史。</summary>
public sealed class TranslationHistoryItem
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string SourceLang { get; set; } = "ja";
    public string TargetLang { get; set; } = "zh-CN";
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>翻译服务接口：多引擎 + 自动回退，流式回调。</summary>
public interface ITranslationService
{
    event EventHandler<int>? Delta;
    event EventHandler<(int requestId, string text)>? Finished;
    event EventHandler<(int requestId, string error)>? Failed;

    IReadOnlyList<TranslationHistoryItem> History { get; }

    /// <summary>分配一个新的请求 ID。</summary>
    int NextRequestId();

    /// <summary>翻译并写入历史，事件通知界面（手动翻译用）。</summary>
    Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "zh-CN",
        int requestId = 0, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>只翻译、不写历史、不发事件（实时字幕等内部用途）。</summary>
    Task<string> TranslateOnceAsync(string text, string sourceLang, string targetLang,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>取消指定请求（流式读取会尽快结束）。</summary>
    void Cancel(int requestId);
}

/// <summary>
/// 翻译服务：把请求交给「当前引擎 + 回退链」。
///
/// 引擎与优先级（config.json → translate）：
///   translate.engine   = index | zhipu | deepseek | google | bing   （默认 index，免 Key）
///   translate.fallback = 逗号分隔的回退顺序                        （默认 google）
/// 需要 Key 的引擎未配置 Key 时会自动跳过；全部失败才报错。
/// </summary>
public class TranslationService : ITranslationService, IDisposable
{
    private const int TotalDeadlineS = 60;      // 一次翻译（含回退）的总时限
    private const int PerEngineTimeoutS = 30;   // 单个引擎的上限
    private const int HistoryMax = 30;
    private const int TextMax = 4000;

    private readonly IConfigService _config;
    private readonly string _historyPath;
    private readonly List<TranslationHistoryItem> _history = new();
    private readonly HashSet<int> _cancelled = new();
    private readonly object _lock = new();
    private int _nextId;
    private bool _disposed;

    public event EventHandler<int>? Delta;
    public event EventHandler<(int requestId, string text)>? Finished;
    public event EventHandler<(int requestId, string error)>? Failed;

    public IReadOnlyList<TranslationHistoryItem> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public TranslationService(IConfigService config, string historyPath)
    {
        _config = config;
        _historyPath = historyPath;
        LoadHistory();
    }

    public int NextRequestId() => Interlocked.Increment(ref _nextId);

    public void Cancel(int requestId)
    {
        lock (_lock) _cancelled.Add(requestId);
    }

    private bool IsCancelled(int requestId)
    {
        lock (_lock) return _cancelled.Contains(requestId);
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja",
        string targetLang = "zh-CN", int requestId = 0,
        CancellationToken cancellationToken = default)
    {
        text = Normalize(text);
        if (text.Length == 0) return "";

        var chain = TranslationEngines.Chain(_config);
        if (chain.Count == 0)
        {
            var msg = "没有可用的翻译引擎：请在设置 →「翻译设置」里选择引擎或填入 API Key";
            Failed?.Invoke(this, (requestId, msg));
            return "";
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(TotalDeadlineS);
        var errors = new List<string>();

        foreach (var engine in chain)
        {
            var sb = new StringBuilder();
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("翻译总超时（60s）");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(Math.Min(PerEngineTimeoutS, remaining.TotalSeconds)));

                var result = await engine.TranslateAsync(text, sourceLang, targetLang, _config,
                    piece =>
                    {
                        sb.Append(piece);
                        Delta?.Invoke(this, requestId);
                    }, linked.Token);

                if (string.IsNullOrWhiteSpace(result))
                    throw new InvalidOperationException("返回空译文");

                AddHistory(text, result, sourceLang, targetLang);
                Finished?.Invoke(this, (requestId, result));
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsCancelled(requestId))
            {
                Failed?.Invoke(this, (requestId, "翻译已取消"));
                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                // 单引擎超时 → 换下一个
                errors.Add($"{engine.Name}: 超时");
            }
            catch (Exception ex)
            {
                errors.Add($"{engine.Name}: {ex.Message}");
            }
        }

        var message = "翻译失败：" + string.Join("；", errors);
        Failed?.Invoke(this, (requestId, message));
        return "";
    }

    public async Task<string> TranslateOnceAsync(string text, string sourceLang, string targetLang,
        CancellationToken cancellationToken = default)
    {
        text = Normalize(text);
        if (text.Length == 0) return "";

        // 整条链的总超时：单引擎超时只保证"单个"不卡死，
        // 5 个引擎依次回退最坏情况会累加到一两分钟——字幕场景不能等这么久。
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(ChainTimeoutS));

        foreach (var engine in TranslationEngines.Chain(_config))
        {
            if (total.IsCancellationRequested) break;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                linked.CancelAfter(TimeSpan.FromSeconds(PerEngineTimeoutS));
                var result = await engine.TranslateAsync(text, sourceLang, targetLang, _config,
                    null, linked.Token);
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return "";
            }
            catch (OperationCanceledException)
            {
                // 单引擎超时或整链超时：继续下一个引擎（若还有时间）
                Debug.WriteLine($"[Translate] {engine.Id}: 超时（{PerEngineTimeoutS}s）");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Translate] {engine.Id}: {ex.Message}");
            }
        }
        return "";
    }

    /// <summary>整条回退链的总超时（秒）：实时字幕等不起"五个引擎各自 8 秒"。</summary>
    private const int ChainTimeoutS = 15;

    private static string Normalize(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > TextMax) t = t[..TextMax];
        return t;
    }

    private void AddHistory(string source, string translation, string sourceLang, string targetLang)
    {
        lock (_lock)
        {
            _history.Insert(0, new TranslationHistoryItem
            {
                Id = 0,
                Source = source,
                Translation = translation,
                SourceLang = sourceLang,
                TargetLang = targetLang,
                Time = DateTime.Now,
            });
            while (_history.Count > HistoryMax) _history.RemoveAt(_history.Count - 1);
            for (var i = 0; i < _history.Count; i++) _history[i].Id = i + 1;
        }
        SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            if (!System.IO.File.Exists(_historyPath)) return;
            var items = System.Text.Json.JsonSerializer.Deserialize<List<TranslationHistoryItem>>(
                System.IO.File.ReadAllText(_historyPath));
            if (items == null) return;
            lock (_lock)
            {
                _history.Clear();
                _history.AddRange(items);
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            List<TranslationHistoryItem> snapshot;
            lock (_lock) snapshot = _history.ToList();
            var dir = System.IO.Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            if (!AtomicFile.WriteAllText(_historyPath, json, out var error))
                App.DebugNote("translate-history: 保存失败 " + error);
        }
        catch (Exception ex) { App.DebugNote("translate-history: 保存异常 " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
