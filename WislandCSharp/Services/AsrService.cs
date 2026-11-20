using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// ASR 路由：按配置在 SenseVoice / Whisper 之间选择，并提供统一的识别入口。
///
///   live.asr.engine = auto | sensevoice | whisper
///     auto：SenseVoice 模型已下载就优先用它（更快），否则用 Whisper
///   live.asr.model  = auto | tiny | base | small | turbo | anime   （Whisper 家族）
///   live.asr.gpu    = auto | off | vulkan | cuda                  （Whisper 家族）
///
/// SenseVoice 加载失败时自动回退到 Whisper（若已有模型）。
/// </summary>
public sealed class AsrService : IDisposable
{
    private readonly IConfigService _cfg;
    private readonly AsrModelStore _store;
    private WhisperAsr? _whisper;
    private SenseVoiceAsr? _senseVoice;
    private volatile bool _disposed;

    public AsrService(IConfigService cfg, AsrModelStore store)
    {
        _cfg = cfg;
        _store = store;
    }

    /// <summary>当前实际使用的引擎：whisper / sensevoice。</summary>
    public string Engine { get; private set; } = "";

    /// <summary>当前实际使用的模型 key。</summary>
    public string ModelKey { get; private set; } = "";

    public bool IsSenseVoice => _senseVoice != null;

    public string EngineLabel => Engine switch
    {
        AsrModels.FamilySenseVoice => "SenseVoice Small",
        AsrModels.FamilyWhisper => $"Whisper {ModelKey}",
        _ => "未加载",
    };

    // ---------------- 选择 ----------------

    private static string EnginePreference(IConfigService cfg) =>
        (cfg.Get("live.asr.engine", "auto") ?? "auto").Trim().ToLowerInvariant();

    /// <summary>
    /// auto 时按语言选择：SenseVoice 对 中文/英文/韩语/粤语 又快又准（实测 0.2s/句），
    /// 但日语效果明显差（实测会漏字/串词），所以日语与「自动语言」都走 Whisper。
    /// </summary>
    public static string ResolveEngine(IConfigService cfg, AsrModelStore store)
    {
        var pref = EnginePreference(cfg);
        if (pref == AsrModels.FamilySenseVoice) return AsrModels.FamilySenseVoice;
        if (pref == AsrModels.FamilyWhisper) return AsrModels.FamilyWhisper;

        var lang = (cfg.Get("live.asr.language", "auto") ?? "auto").Trim().ToLowerInvariant();
        var senseVoiceStrong = lang is "zh" or "zh-cn" or "zh-tw" or "zh-hans" or "zh-hant"
                                   or "en" or "ko" or "yue";
        return senseVoiceStrong && store.Exists("sensevoice")
            ? AsrModels.FamilySenseVoice
            : AsrModels.FamilyWhisper;
    }

    /// <summary>按配置 + 模型存在情况，决定本次要加载的模型 key。</summary>
    public static string ResolveModelKey(IConfigService cfg, AsrModelStore store)
    {
        var engine = ResolveEngine(cfg, store);
        if (engine == AsrModels.FamilySenseVoice) return "sensevoice";

        var model = AsrModels.ResolveWhisperModel(cfg.Get("live.asr.model", "auto"));
        // 配置的 Whisper 模型没下载时，退到已存在的任意 Whisper 模型
        if (store.Exists(model)) return model;
        foreach (var info in AsrModels.Family(AsrModels.FamilyWhisper))
        {
            if (store.Exists(info.Key)) return info.Key;
        }
        return model;
    }

    // ---------------- 加载 ----------------

    /// <summary>加载模型；成功返回 true。SenseVoice 失败会自动尝试 Whisper。</summary>
    public bool Load(string modelKey)
    {
        var info = AsrModels.Find(modelKey);
        if (info == null)
        {
            Debug.WriteLine($"[Asr] 未知模型: {modelKey}");
            return false;
        }

        if (info.Family == AsrModels.FamilySenseVoice)
        {
            if (TryLoadSenseVoice()) return true;
            // 回退 Whisper：优先配置的 whisper 模型，其次任意已存在模型
            var fallback = AsrModels.ResolveWhisperModel(_cfg.Get("live.asr.model", "auto"));
            if (_store.Exists(fallback) && TryLoadWhisper(fallback)) return true;
            foreach (var w in AsrModels.Family(AsrModels.FamilyWhisper))
            {
                if (_store.Exists(w.Key) && TryLoadWhisper(w.Key)) return true;
            }
            return false;
        }

        return TryLoadWhisper(info.Key);
    }

    private bool TryLoadSenseVoice()
    {
        try
        {
            var threads = _cfg.Get("live.asr.threads", 0);
            var language = _cfg.Get("live.asr.language", "auto");
            var asr = new SenseVoiceAsr(_store, threads, language);
            asr.Load();
            _senseVoice = asr;
            Engine = AsrModels.FamilySenseVoice;
            ModelKey = "sensevoice";
            Debug.WriteLine($"[Asr] 已加载 SenseVoice（language={language}）");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Asr] SenseVoice 加载失败: " + ex.Message);
            return false;
        }
    }

    private bool TryLoadWhisper(string modelKey)
    {
        try
        {
            var gpu = (_cfg.Get("live.asr.gpu", "auto") ?? "auto").Trim().ToLowerInvariant();
            WhisperAsr.ConfigureRuntime(gpu);

            var asr = new WhisperAsr(_store.Dir);
            asr.SetThreads(_cfg.Get("live.asr.threads", 0));
            asr.UseModel(modelKey);
            _whisper = asr;
            Engine = AsrModels.FamilyWhisper;
            ModelKey = modelKey;
            Debug.WriteLine($"[Asr] 已选择 Whisper {modelKey}（gpu={gpu}）");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Asr] Whisper 初始化失败: " + ex.Message);
            return false;
        }
    }

    // ---------------- 识别 ----------------

    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct)
    {
        if (_disposed) return "";
        if (_senseVoice != null)
        {
            // SenseVoice 是同步 CPU 推理（通常几十毫秒到几百毫秒），放到线程池避免阻塞 VAD 循环
            return await Task.Run(() => _senseVoice.Transcribe(samples), ct);
        }
        if (_whisper != null)
            return await _whisper.TranscribeAsync(samples, language, ct);
        return "";
    }

    public void Dispose()
    {
        _disposed = true;
        try { _senseVoice?.Dispose(); } catch { }
        try { _whisper?.Dispose(); } catch { }
        _senseVoice = null;
        _whisper = null;
    }
}
