using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// 翻译引擎抽象。一次翻译请求在各引擎间统一成：
/// 「边产出边回调 onDelta（流式引擎逐 token，非流式引擎最后一次性回调）」。
///
/// 已接入：
///   index    — B站 Index-Translate 官方免费 API（OpenAI 兼容、免 Key、支持流式）
///   zhipu    — 智谱 GLM-4-Flash（OpenAI 兼容、需 Key、免费模型）
///   deepseek — DeepSeek（OpenAI 兼容、需 Key、流式，保留原有长提示词）
///   google   — Google 免密钥网页接口（translate_a/single）
///   bing     — Bing 免密钥网页接口（Edge 官方接口 2026-07 下线后的替代）
/// </summary>
public interface ITranslationEngine
{
    string Id { get; }
    string Name { get; }
    bool NeedsKey { get; }
    bool Available(IConfigService cfg);

    Task<string> TranslateAsync(string text, string sourceLang, string targetLang,
        IConfigService cfg, Action<string>? onDelta, CancellationToken ct);
}

public static class TranslationEngines
{
    public const string Index = "index";
    public const string Zhipu = "zhipu";
    public const string DeepSeek = "deepseek";
    public const string Google = "google";
    public const string Bing = "bing";

    public const string DefaultEngine = Index;
    public const string DefaultFallback = "google";

    private static readonly ITranslationEngine[] AllEngines =
    {
        new IndexEngine(),
        new ZhipuEngine(),
        new DeepSeekEngine(),
        new GoogleEngine(),
        new BingEngine(),
    };

    public static IReadOnlyList<ITranslationEngine> All => AllEngines;

    public static ITranslationEngine? Find(string? id) =>
        AllEngines.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>当前引擎 + 回退链（自动跳过"需要 Key 但没配"的引擎）。</summary>
    public static List<ITranslationEngine> Chain(IConfigService cfg)
    {
        var result = new List<ITranslationEngine>();
        var primary = Find(cfg.Get("translate.engine", DefaultEngine))
                      ?? Find(DefaultEngine);
        if (primary != null && primary.Available(cfg)) result.Add(primary);

        var fallbackRaw = cfg.Get("translate.fallback", DefaultFallback) ?? DefaultFallback;
        foreach (var part in fallbackRaw.Split(new[] { ',', ';', '，', ' ' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var e = Find(part.Trim());
            if (e == null) continue;
            if (result.Any(x => x.Id == e.Id)) continue;
            if (!e.Available(cfg)) continue;
            result.Add(e);
        }
        return result;
    }

    /// <summary>目标语言的提示词写法。</summary>
    public static string LangWord(string code) => code switch
    {
        "zh-CN" or "zh" or "zh-Hans" => "简体中文",
        "zh-TW" or "zh-Hant" => "繁體中文",
        "ja" => "日语",
        "en" => "英语",
        "ko" => "韩语",
        _ => code,
    };

    /// <summary>去掉大模型偶尔吐出的思考标记/特殊 token。</summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Regex.Replace(text, @"<(think|thinking|reasoning)>[\s\S]*?</\1>",
            "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\|[^|>]*\|>", "");
        s = Regex.Replace(s, @"^```[a-zA-Z]*\s*", "");
        s = Regex.Replace(s, @"\s*```$", "");
        return s.Trim();
    }

    /// <summary>
    /// 清洗译文：大模型（尤其 GLM-4-Flash）偶尔会输出「名词解释/例句/注释」，
    /// 这里去掉标签前缀、并在出现解释性内容前截断，只保留第一段译文。
    /// </summary>
    public static string SanitizeTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Clean(text);

        // 去掉「译文：」「翻译：」「中文：」等前缀
        var labels = new[] { "译文：", "译文:", "翻译：", "翻译:", "中文：", "中文:",
                             "translation:", "translation：", "译：" };
        foreach (var label in labels)
        {
            if (t.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                t = t[label.Length..].TrimStart();
                break;
            }
        }

        // 出现解释性段落就截断（保留此前的译文）
        var markers = new[] { "（注", "(注", "【注", "注：", "注:", "说明：", "解释：", "释义：",
                             "该词", "这个词", "意思是", "意为", "词性", "例句", "读音", "发音",
                             "罗马音", "罗马字", "这里指", "在此处表示", "可译为" };
        foreach (var m in markers)
        {
            var idx = t.IndexOf(m, StringComparison.Ordinal);
            if (idx > 0) { t = t[..idx].TrimEnd(); break; }
        }

        // 去掉首尾引号（模型偶尔包一层）
        t = t.Trim().Trim('"', '“', '”', '「', '」').Trim();

        // 多段时只保留第一段（字幕场景不要解释性第二段）
        var lines = t.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1) t = lines[0].Trim();

        return t;
    }

    /// <summary>译文里是否混入了"名词解释/词性/例句"这类内容（GLM-4-Flash 偶尔会这样）。</summary>
    public static bool LooksLikeExplanation(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var markers = new[] { "该词", "这个词", "意思是", "意为", "词性", "例句", "读音", "发音",
                              "罗马音", "罗马字", "解释", "释义", "（注", "(注", "【注",
                              "是日语", "日语中", "这里指", "在此处", "可译为" };
        foreach (var m in markers)
            if (raw.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>构造"指令 + 正文"的用户消息（Index / 网页引擎用）。</summary>
    public static string InstructionContent(string text, string sourceLang, string targetLang)
    {
        var tgt = LangWord(targetLang);
        if (sourceLang is "ja" or "auto" or "")
            return $"请将以下文本翻译为{tgt}，直接输出翻译结果，不要进行任何解释。\n\n{text}";
        return $"请将以下{sourceLang}文本翻译为{tgt}，直接输出翻译结果，不要进行任何解释。\n\n{text}";
    }

    /// <summary>读取配置里的 Key；支持 translate.import_from 指向旧配置文件。</summary>
    public static string KeyFrom(IConfigService cfg, string key, string legacyImportKey)
    {
        var value = (cfg.Get(key, "") ?? "").Trim();
        if (value.Length > 0) return value;

        var importFrom = cfg.Get("translate.import_from", "") ?? "";
        if (importFrom.Length == 0 || !System.IO.File.Exists(importFrom)) return "";
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(importFrom));
            if (doc.RootElement.TryGetProperty("translate", out var t) &&
                t.TryGetProperty(legacyImportKey, out var k))
                return k.GetString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }
}

// ============================================================
// OpenAI 兼容引擎（Index / 智谱 / DeepSeek）
// ============================================================

internal class OpenAiCompatEngine : ITranslationEngine
{
    // 引擎是全局单例，HttpClient 复用即可（超时由调用方的 CancellationToken 控制）
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Func<IConfigService, (string BaseUrl, string Model, string Key)> _conn;
    private readonly string? _systemPrompt;
    private readonly Func<string, string, string, string> _userContent;
    private readonly double _temperature;
    private readonly bool _disableThinking;
    private readonly string? _userAgent;

    public OpenAiCompatEngine(
        string id, string name, bool needsKey,
        Func<IConfigService, (string, string, string)> conn,
        Func<string, string, string, string> userContent,
        string? systemPrompt = null,
        double temperature = 0.3,
        bool disableThinking = false,
        string? userAgent = null)
    {
        Id = id;
        Name = name;
        NeedsKey = needsKey;
        _conn = conn;
        _userContent = userContent;
        _systemPrompt = systemPrompt;
        _temperature = temperature;
        _disableThinking = disableThinking;
        _userAgent = userAgent;
    }

    public string Id { get; }
    public string Name { get; }
    public bool NeedsKey { get; }

    public bool Available(IConfigService cfg)
    {
        if (!NeedsKey) return true;
        var (_, _, key) = _conn(cfg);
        return !string.IsNullOrWhiteSpace(key);
    }

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang,
        IConfigService cfg, Action<string>? onDelta, CancellationToken ct)
    {
        var (baseUrl, model, key) = _conn(cfg);
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("接口地址为空");
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("模型名为空");
        if (NeedsKey && string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("未配置 API Key");

        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(_systemPrompt))
            messages.Add(new { role = "system", content = _systemPrompt });
        messages.Add(new { role = "user", content = _userContent(text, sourceLang, targetLang) });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = _temperature,
            ["max_tokens"] = 2048,
            ["stream"] = true,
        };
        if (_disableThinking) payload["thinking"] = new { type = "disabled" };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        using var response = await Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"{Name} 返回 {(int)response.StatusCode}：{Truncate(body, 160)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sb = new StringBuilder();
        var sawDone = false;
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (line.Length == 0) continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // 少数实现会直接把整段 JSON 返回（非 SSE）
                if (line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    TryExtractWholeJson(line, sb, onDelta);
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]") { sawDone = true; break; }
            TryExtractChunk(data, sb, onDelta);
        }

        var rawText = sb.ToString();

        // 完整性：既没有 [DONE]、也没有拿到任何有效内容 → 视为断流/截断，交给下一个引擎。
        // 原实现会把"半个句子"当成功译文返回（SSE 断流时 JSON 解析异常被吞掉），用户看到的是残缺译文。
        if (rawText.Length == 0)
            throw new InvalidDataException(sawDone ? "服务端返回空译文" : "流式响应中断且没有内容");
        if (!sawDone)
            Debug.WriteLine("[Translate] SSE 未收到 [DONE]，按已收内容处理（可能被截断）");

        if (TranslationEngines.LooksLikeExplanation(rawText))
            throw new InvalidDataException("译文包含解释性内容，换下一个引擎");

        return TranslationEngines.SanitizeTranslation(rawText);
    }

    private static void TryExtractChunk(string data, StringBuilder sb, Action<string>? onDelta)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return;
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (!delta.TryGetProperty("content", out var cp)) continue;
                var piece = cp.GetString();
                if (string.IsNullOrEmpty(piece)) continue;
                sb.Append(piece);
                onDelta?.Invoke(piece);
            }
        }
        catch { }
    }

    private static void TryExtractWholeJson(string json, StringBuilder sb, Action<string>? onDelta)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0) return;
            if (!choices[0].TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var cp) || cp.ValueKind != JsonValueKind.String) return;
            var piece = cp.GetString() ?? "";
            if (piece.Length == 0) return;
            sb.Append(piece);
            onDelta?.Invoke(piece);
        }
        catch { }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

internal sealed class IndexEngine : OpenAiCompatEngine
{
    public IndexEngine() : base(
        TranslationEngines.Index,
        "Index-Translate（B站，免 Key）",
        needsKey: false,
        cfg => (
            (cfg.Get("translate.index_base_url", "https://index-translate.bilibili.com/v1") ?? "")
                .Trim(),
            (cfg.Get("translate.index_model", "Index-Translate-35B-A3B") ?? "").Trim(),
            ""),
        (text, src, tgt) => TranslationEngines.InstructionContent(text, src, tgt),
        systemPrompt: null,
        temperature: 0.2,
        disableThinking: false,
        userAgent: "Index-Translate-Client/1.0 (Wisland)")
    { }
}

internal sealed class ZhipuEngine : OpenAiCompatEngine
{
    public ZhipuEngine() : base(
        TranslationEngines.Zhipu,
        "智谱 GLM-4-Flash",
        needsKey: true,
        cfg => (
            "https://open.bigmodel.cn/api/paas/v4",
            (cfg.Get("translate.zhipu_model", "glm-4-flash") ?? "glm-4-flash").Trim(),
            TranslationEngines.KeyFrom(cfg, "translate.zhipu_api_key", "zhipu_api_key")),
        (text, src, tgt) => TranslationEngines.InstructionContent(text, src, tgt),
        systemPrompt: "你是字幕翻译引擎，只做一件事：把用户给出的文本翻译成指定目标语言，直接输出译文。"
                      + "严禁输出任何解释、词义说明、注释、例句、词性、读音、罗马音、原文对照或「翻译：」之类的标签。"
                      + "不要加引号，不要输出括号补充说明，不要换行分段。"
                      + "如果内容无法翻译，只输出空字符串。保留原文的换行与语气。",
        temperature: 0.3)
    { }
}

internal sealed class DeepSeekEngine : OpenAiCompatEngine
{
    public DeepSeekEngine() : base(
        TranslationEngines.DeepSeek,
        "DeepSeek",
        needsKey: true,
        cfg => (
            (cfg.Get("translate.base_url", "https://api.deepseek.com") ?? "").Trim(),
            (cfg.Get("translate.model", "deepseek-flash") ?? "").Trim(),
            TranslationEngines.KeyFrom(cfg, "translate.api_key", "api_key")),
        (text, src, tgt) => text,
        systemPrompt: DeepSeekPrompt,
        temperature: 1.0,
        disableThinking: true)
    { }

    private const string DeepSeekPrompt = @"
你是一名专业的日译中翻译助手，擅长将日语内容准确、自然地翻译成中文。请严格遵循以下要求：
1. 准确传达原意，不添加、不删减原文没有的信息；
2. 语言自然流畅，符合中文表达习惯；
3. 保留专有名词、人名、地名等原文拼写，除非有通用中文译名；
4. 避免使用生僻字或过于文言化的表达；
5. 保持翻译的简洁性，不使用冗长的解释性语句；
6. 对于口语化表达，使用对应的口语化中文；
7. 保持段落结构与原文一致；
8. 标点符号使用中文全角标点；
9. 不输出除翻译结果外的任何说明文字；
10. 保持原文的语气和情感色彩；
11. 不输出中英混杂的翻译；
12. 翻译结果不得包含原文日文。";
}

// ============================================================
// 免密钥网页引擎（Google / Bing）
// ============================================================

internal sealed class GoogleEngine : ITranslationEngine
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        return c;
    }

    public string Id => TranslationEngines.Google;
    public string Name => "Google（免 Key）";
    public bool NeedsKey => false;
    public bool Available(IConfigService cfg) => true;

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang,
        IConfigService cfg, Action<string>? onDelta, CancellationToken ct)
    {
        var sl = string.IsNullOrWhiteSpace(sourceLang) ? "auto" : sourceLang;
        var q = Uri.EscapeDataString(text);
        var url = "https://translate.googleapis.com/translate_a/single"
                  + $"?client=gtx&sl={sl}&tl={targetLang}&dt=t&q={q}";

        using var resp = await Client.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Google 接口限流（429）");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Google 返回 {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException("Google 响应格式异常");

        var sb = new StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Array || seg.GetArrayLength() == 0) continue;
            if (seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
        }
        var result = sb.ToString().Trim();
        if (result.Length == 0) throw new InvalidOperationException("Google 返回空译文");
        result = TranslationEngines.SanitizeTranslation(result);
        onDelta?.Invoke(result);
        return result;
    }
}

/// <summary>
/// Bing 免密钥翻译：抓 translator 页面拿 IG / IID / abuse 校验参数，再请求 ttranslatev3。
/// （Edge 官方 token 接口已于 2026-07-30 下线，这是社区通行的替代做法）
/// </summary>
internal sealed class BingEngine : ITranslationEngine
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return c;
    }

    private static readonly object TokenLock = new();
    private static (string Ig, string Iid, string Token, string Key, DateTime Expiry) _cached;

    public string Id => TranslationEngines.Bing;
    public string Name => "Bing（免 Key）";
    public bool NeedsKey => false;
    public bool Available(IConfigService cfg) => true;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/126.0.0.0 Safari/537.36";

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang,
        IConfigService cfg, Action<string>? onDelta, CancellationToken ct)
    {
        var target = MapTarget(targetLang);
        var result = await TranslateOnce(Client, text, sourceLang, target, ct);
        if (result == null)
        {
            // token 可能过期：清缓存再试一次
            lock (TokenLock) _cached = default;
            result = await TranslateOnce(Client, text, sourceLang, target, ct);
        }
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("Bing 返回空译文");

        result = TranslationEngines.SanitizeTranslation(result);
        onDelta?.Invoke(result);
        return result;
    }

    private static async Task<string?> TranslateOnce(HttpClient client, string text,
        string sourceLang, string target, CancellationToken ct)
    {
        var token = await EnsureTokenAsync(client, ct);
        if (token.Ig.Length == 0) return null;

        var form = new List<KeyValuePair<string, string>>
        {
            new("fromLang", string.IsNullOrWhiteSpace(sourceLang) || sourceLang == "auto" ? "auto" : sourceLang),
            new("to", target),
            new("text", text),
            new("token", token.Token),
            new("key", token.Key),
        };
        var url = $"https://www.bing.com/ttranslatev3?isVertical=1&IG={token.Ig}&IID={token.Iid}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bing.com/translator");

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;
        var first = doc.RootElement[0];
        if (!first.TryGetProperty("translations", out var trs) || trs.GetArrayLength() == 0)
            return null;
        if (!trs[0].TryGetProperty("text", out var tp)) return null;
        return TranslationEngines.Clean(tp.GetString() ?? "");
    }

    private static async Task<(string Ig, string Iid, string Token, string Key, DateTime Expiry)>
        EnsureTokenAsync(HttpClient client, CancellationToken ct)
    {
        lock (TokenLock)
        {
            if (_cached.Expiry > DateTime.UtcNow && _cached.Ig.Length > 0) return _cached;
        }

        using var resp = await client.GetAsync("https://www.bing.com/translator", ct);
        if (!resp.IsSuccessStatusCode) return default;
        var html = await resp.Content.ReadAsStringAsync(ct);

        var ig = Regex.Match(html, "IG:\"([^\"]+)\"").Groups[1].Value;
        var iid = Regex.Match(html, "data-iid=\"([^\"]+)\"").Groups[1].Value;
        var abuse = Regex.Match(html,
            "params_AbusePreventionHelper\\s*=\\s*\\[\\s*(\\d+)\\s*,\\s*\"([^\"]+)\"");
        if (ig.Length == 0 || iid.Length == 0 || !abuse.Success) return default;

        var value = (ig, iid, abuse.Groups[2].Value, abuse.Groups[1].Value,
            DateTime.UtcNow.AddMinutes(8));
        lock (TokenLock) _cached = value;
        return value;
    }

    private static string MapTarget(string targetLang) => targetLang switch
    {
        "zh-CN" or "zh" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        _ => targetLang,
    };
}
