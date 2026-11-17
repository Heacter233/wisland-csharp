using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>翻译历史记录项（对应 Python translate_history.json 条目）。</summary>
public sealed class TranslationHistoryItem
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string SourceLang { get; set; } = "ja";
    public string TargetLang { get; set; } = "zh-CN";
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>翻译服务接口：DeepSeek API 流式翻译。</summary>
public interface ITranslationService
{
    event EventHandler<int>? Delta;
    event EventHandler<(int requestId, string text)>? Finished;
    event EventHandler<(int requestId, string error)>? Failed;

    IReadOnlyList<TranslationHistoryItem> History { get; }

    /// <summary>分配一个新的请求 ID。</summary>
    int NextRequestId();

    Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "zh-CN",
        int requestId = 0, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>取消指定请求（流式读取会尽快结束）。</summary>
    void Cancel(int requestId);
}

/// <summary>
/// 翻译服务：DeepSeek API 封装，支持 SSE 流式输出、60s 总时限、取消、历史持久化。
/// 对应 Python translate.py 的 TranslateService。
/// </summary>
public class TranslationService : ITranslationService, IDisposable
{
    private const int TotalDeadlineS = 60;
    private const int HistoryMax = 30;
    private const int TextMax = 4000;

    private static readonly string SystemPrompt = @"
你是一名专业的日译中翻译助手，擅长将日语内容准确、自然地翻译成中文。请严格遵循以下要求：
1. 准确传达原意，不添加、不删减原文没有的信息；
2. 语言自然流畅，符合中文表达习惯；
3. 保留专有名词、人名、地名等原文拼写，除非有通用中文译名；
4. 避免使用生僻字或过于文言化的表达；
5. 保持翻译的简洁性，不使用冗长的解释性语句；
6. 对于口语化表达，使用对应的口语化中文；
7. 对于书面语，使用相应的书面语中文；
8. 保持段落结构与原文一致；
9. 标点符号使用中文全角标点；
10. 数字、日期、时间等按中文习惯翻译；
11. 对于具有文化内涵的词汇，可适当做简要解释但不影响阅读；
12. 不输出除翻译结果外的任何说明文字；
13. 不添加翻译后的作者注、备注或免责声明；
14. 不使用翻译软件常见的机械化句式；
15. 保持原文的语气和情感色彩；
16. 对于包含方言或地域特色的表达，尽量保留其特色；
17. 不输出中英混杂的翻译；
18. 对于不确定的词汇，使用最可能的中文对应词，不加注释；
19. 保持原文的换行和分段；
20. 翻译结果不得包含原文日文。";

    private readonly System.Net.Http.HttpClient _http = new();
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

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "zh-CN",
        int requestId = 0, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            var err = "未配置 DeepSeek API Key（config.json → translate.api_key）";
            Failed?.Invoke(this, (requestId, err));
            return "";
        }

        var baseUrl = _config.Get("translate.base_url", "https://api.deepseek.com");
        var model = _config.Get("translate.model", "deepseek-flash");

        text = text.Trim();
        if (text.Length > TextMax) text = text[..TextMax];
        if (text.Length == 0) return "";

        var deadline = DateTimeOffset.UtcNow.AddSeconds(TotalDeadlineS);
        var sb = new System.Text.StringBuilder();

        try
        {
            var payload = new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = BuildUserContent(text, sourceLang, targetLang) }
                },
                temperature = 1.0,
                thinking = new { type = "disabled" },
                max_tokens = 2000,
                stream = true,
                stream_options = new { include_usage = true }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
            { Content = content };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _http.SendAsync(request,
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var msg = $"翻译请求失败 ({(int)response.StatusCode}): {Truncate(body, 200)}";
                Failed?.Invoke(this, (requestId, msg));
                return "";
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCancelled(requestId)) throw new OperationCanceledException();
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("翻译超时（60s）");

                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var chunk = System.Text.Json.JsonDocument.Parse(data);
                    if (!chunk.RootElement.TryGetProperty("choices", out var choices)) continue;
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (!choice.TryGetProperty("delta", out var delta)) continue;
                        if (!delta.TryGetProperty("content", out var cp)) continue;
                        var piece = cp.GetString();
                        if (string.IsNullOrEmpty(piece)) continue;
                        sb.Append(piece);
                        Delta?.Invoke(this, requestId);
                    }
                }
                catch { }
            }

            var result = sb.ToString();
            AddHistory(text, result, sourceLang, targetLang);
            Finished?.Invoke(this, (requestId, result));
            return result;
        }
        catch (OperationCanceledException)
        {
            Failed?.Invoke(this, (requestId, "翻译已取消"));
            return sb.ToString();
        }
        catch (TimeoutException ex)
        {
            Failed?.Invoke(this, (requestId, ex.Message));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, (requestId, ex.Message));
            return sb.ToString();
        }
        finally
        {
            lock (_lock) _cancelled.Remove(requestId);
        }
    }

    private static string BuildUserContent(string text, string sourceLang, string targetLang)
    {
        if (sourceLang == "ja" && targetLang == "zh-CN") return text;
        return $"请将以下内容从 {sourceLang} 翻译为 {targetLang}：\n\n{text}";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private string GetApiKey()
    {
        var key = _config.Get("translate.api_key", "");
        if (!string.IsNullOrWhiteSpace(key)) return key.Trim();

        var importFrom = _config.Get("translate.import_from", "");
        if (!string.IsNullOrWhiteSpace(importFrom) && System.IO.File.Exists(importFrom))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(importFrom));
                if (doc.RootElement.TryGetProperty("translate", out var t) &&
                    t.TryGetProperty("api_key", out var k))
                    return k.GetString()?.Trim() ?? "";
            }
            catch { }
        }
        return "";
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
            System.IO.File.WriteAllText(_historyPath,
                System.Text.Json.JsonSerializer.Serialize(snapshot,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}