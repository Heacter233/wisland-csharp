using System;
using System.Collections.Generic;
using System.IO;
using SherpaOnnx;

namespace Wisland.Services;

/// <summary>
/// 人声处理（sherpa-onnx speech enhancement）：原始音频 → 增强 → VAD → ASR。
///
/// 支持三种后端（模型由模型页按需下载）：
///   dpdfnet  降噪（推荐，保留所有人声，去背景音乐/噪声）
///   gtcrn    轻量降噪
///   voxlock  目标说话人提取（只保留锁定的第一个说话人；多人对话会丢台词）
///
/// 内部用 OnlineSpeechDenoiser 流式处理：输入任意长度块，按 FrameShiftInSamples 切片喂给模型，
/// 输出再做一次缓慢的电平归一化（实测 DPDFNet 输出电平会掉 8~18dB，不归一化 VAD 会漏句）。
/// </summary>
public sealed class AudioEnhancer : IDisposable
{
    public const string ModeOff = "off";

    private OnlineSpeechDenoiser? _denoiser;
    private int _frame = 160;
    private readonly List<float> _pending = new();
    private readonly List<float> _out = new();

    private bool _normalize = true;
    private float _gain = 1f;

    public string Mode { get; private set; } = ModeOff;

    /// <summary>加载失败原因（空 = 正常）。</summary>
    public string? Error { get; private set; }

    public bool Active => _denoiser != null;

    public static bool IsEnhanceFamily(string? key) =>
        AsrModels.Find(key)?.Family == AsrModels.FamilyEnhance;

    /// <summary>模式 → 模型 key（模型页里下载的 key）。</summary>
    public static string ModelKeyForMode(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "dpdfnet" or "dpdfnet2" => "dpdfnet2",
        "gtcrn" => "gtcrn",
        "voxlock" => "voxlock",
        _ => "",
    };

    public static string DisplayName(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "dpdfnet" => "DPDFNet2",
        "gtcrn" => "GTCRN",
        "voxlock" => "VoxLock",
        _ => "关闭",
    };

    /// <summary>加载增强模型。mode=off 时返回 true 且不做处理。</summary>
    public bool Load(string mode, string modelsDir, int threads, string? voiceSamplePath, bool normalize)
    {
        Dispose();
        Mode = (mode ?? ModeOff).Trim().ToLowerInvariant();
        _normalize = normalize;
        Error = null;

        if (Mode is ModeOff or "" or "none") { Mode = ModeOff; return true; }

        var key = ModelKeyForMode(Mode);
        if (key.Length == 0)
        {
            Error = "未知人声处理模式：" + mode;
            return false;
        }

        var store = new AsrModelStore(modelsDir);
        if (!store.Exists(key))
        {
            Error = $"人声处理模型未下载：{AsrModels.Find(key)?.DisplayName ?? key}";
            return false;
        }

        try
        {
            var config = new OnlineSpeechDenoiserConfig();
            config.Model.NumThreads = Math.Max(1, threads);
            config.Model.Provider = "cpu";
            config.Model.Debug = 0;

            switch (Mode)
            {
                case "voxlock":
                    config.Model.Voxlock.EnrollModel =
                        Path.Combine(modelsDir, "enhance", "voxlock_enroll.onnx");
                    config.Model.Voxlock.ChunkModel =
                        Path.Combine(modelsDir, "enhance", "voxlock_chunk_int8.onnx");
                    if (!string.IsNullOrWhiteSpace(voiceSamplePath) && File.Exists(voiceSamplePath))
                        config.Model.Voxlock.VoiceSample = voiceSamplePath;
                    break;
                case "gtcrn":
                    config.Model.Gtcrn.Model = Path.Combine(modelsDir, "enhance", "gtcrn_simple.onnx");
                    break;
                default: // dpdfnet
                    config.Model.Dpdfnet.Model = Path.Combine(modelsDir, "enhance", "dpdfnet2.onnx");
                    break;
            }

            _denoiser = new OnlineSpeechDenoiser(config);
            _frame = Math.Max(80, _denoiser.FrameShiftInSamples);
            _pending.Clear();
            _out.Clear();
            _gain = 1f;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>流式处理（在音频线程里调用）：返回本次可输出的增强采样（可能为空数组）。</summary>
    public float[] Process(float[] input)
    {
        if (_denoiser == null) return input;
        if (input.Length == 0) return Array.Empty<float>();

        _pending.AddRange(input);
        _out.Clear();

        while (_pending.Count >= _frame)
        {
            var chunk = new float[_frame];
            _pending.CopyTo(0, chunk, 0, _frame);
            _pending.RemoveRange(0, _frame);

            var audio = _denoiser.Run(chunk, AudioCapture.TargetRate);
            if (audio.SampleRate == AudioCapture.TargetRate)
                _out.AddRange(audio.Samples);
            else
                _out.AddRange(audio.Samples);   // sherpa 目前固定输出 16k，保守处理
        }

        if (_out.Count == 0) return Array.Empty<float>();
        var result = _out.ToArray();
        return Normalize(result);
    }

    /// <summary>把增强后的电平拉回 ~ -22 dBFS（目标 RMS 0.08），避免 VAD 因电平骤降而漏句。</summary>
    private float[] Normalize(float[] samples)
    {
        if (!_normalize || samples.Length == 0) return samples;

        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        if (rms < 1e-5) return samples;

        var desired = (float)Math.Clamp(0.08 / rms, 0.5, 12.0);
        var rate = desired > _gain ? 0.3f : 0.05f;   // 快升慢降
        _gain += (desired - _gain) * rate;

        for (var i = 0; i < samples.Length; i++)
            samples[i] = Math.Clamp(samples[i] * _gain, -1f, 1f);
        return samples;
    }

    public void Reset()
    {
        _pending.Clear();
        _out.Clear();
        _gain = 1f;
        try { _denoiser?.Reset(); } catch { }
    }

    public void Dispose()
    {
        try { _denoiser?.Dispose(); } catch { }
        _denoiser = null;
        _pending.Clear();
        _out.Clear();
        Mode = ModeOff;
    }
}
