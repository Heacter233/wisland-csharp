using System;
using System.IO;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace Wisland.Services;

/// <summary>
/// SenseVoice-Small（sherpa-onnx / ONNX Runtime）离线识别。
///
/// 特点：非自回归，CPU 上比 Whisper 快数倍；支持 中文/日语/英语/韩语/粤语；
/// 输入同样是 16kHz 单声道 float。语言在加载时固定（config → live.asr.language）。
/// </summary>
public sealed class SenseVoiceAsr : IDisposable
{
    private const int SampleRate = 16000;

    private readonly AsrModelStore _store;
    private readonly int _threads;
    private readonly string _language;
    private OfflineRecognizer? _recognizer;

    public SenseVoiceAsr(AsrModelStore store, int threads, string language)
    {
        _store = store;
        _threads = threads <= 0 ? Math.Max(2, Environment.ProcessorCount - 1) : threads;
        _language = MapLanguage(language);
    }

    public bool ModelExists => _store.Exists("sensevoice");

    /// <summary>加载模型（失败抛异常，由上层决定回退到 Whisper）。</summary>
    public void Load()
    {
        if (_recognizer != null) return;

        var model = Path.Combine(_store.Dir, "sensevoice", "model.int8.onnx");
        var tokens = Path.Combine(_store.Dir, "sensevoice", "tokens.txt");
        if (!File.Exists(model) || !File.Exists(tokens))
            throw new FileNotFoundException("SenseVoice 模型文件缺失：" + model);

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = _threads;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "sense_voice";
        config.ModelConfig.SenseVoice.Model = model;
        config.ModelConfig.SenseVoice.Language = _language;
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
    }

    /// <summary>识别一段 16kHz 单声道音频（同步；调用方负责串行化）。</summary>
    public string Transcribe(float[] samples)
    {
        if (_recognizer == null) throw new InvalidOperationException("SenseVoice 模型未加载");
        if (samples.Length == 0) return "";

        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        _recognizer.Decode(stream);
        return Clean(stream.Result?.Text ?? "");
    }

    /// <summary>去掉 SenseVoice 输出里的 &lt;|zh|&gt;、&lt;|NEUTRAL|&gt; 之类的控制标记。</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text, @"<\|[^|>]*\|>", "");
        return t.Trim();
    }

    private static string MapLanguage(string language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "ja" or "jp" => "ja",
        "en" => "en",
        "zh" or "zh-cn" or "zh-hans" => "zh",
        "zh-tw" or "zh-hant" => "zh",
        "ko" => "ko",
        "yue" or "cantonese" => "yue",
        _ => "auto",
    };

    public void Dispose()
    {
        try { _recognizer?.Dispose(); } catch { }
        _recognizer = null;
    }
}
