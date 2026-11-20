using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wisland.Services;

/// <summary>一个模型包里的单个文件（Whisper 是单文件；SenseVoice 是 onnx + tokens 两个）。</summary>
public sealed class AsrModelFile
{
    public string LocalPath { get; init; } = "";       // 相对 models 目录
    public string[] Urls { get; init; } = Array.Empty<string>();
    public long SizeBytes { get; init; }
    public bool IsTokenFile { get; init; }
}

/// <summary>一个可下载的 ASR 模型（按引擎家族分组展示）。</summary>
public sealed class AsrModelInfo
{
    public string Key { get; init; } = "";
    public string Family { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Note { get; init; } = "";
    public string Languages { get; init; } = "";
    public string HardwareHint { get; init; } = "";
    public int SizeMb { get; init; }
    public bool RecommendGpu { get; init; }
    public AsrModelFile[] Files { get; init; } = Array.Empty<AsrModelFile>();

    public string PrimaryLocalPath => Files.Length > 0 ? Files[0].LocalPath : "";
    public long TotalBytes => Files.Sum(f => f.SizeBytes);
}

/// <summary>
/// ASR 模型目录：Whisper GGML（Whisper.net）+ SenseVoice ONNX（sherpa-onnx）。
///
/// Whisper 家族本地统一命名 ggml-{key}.bin（兼容已有 tiny/base/small），
/// turbo / anime 只是远端文件名不同。
/// </summary>
public static class AsrModels
{
    public const string FamilyWhisper = "whisper";
    public const string FamilySenseVoice = "sensevoice";
    public const string FamilyEnhance = "enhance";

    private const string WhisperRepo = "ggerganov/whisper.cpp";
    private const string AnimeRepo = "Jaffe2718/ggml-whisper-unofficial";
    private const string SenseVoiceRepo =
        "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09";

    private static string[] Urls(string repo, string file) => new[]
    {
        $"https://hf-mirror.com/{repo}/resolve/main/{file}",
        $"https://huggingface.co/{repo}/resolve/main/{file}",
    };

    /// <summary>GitHub Release 直链（带一个公共镜像回退，方便国内网络）。</summary>
    private static string[] GithubUrls(string repo, string tag, string file) => new[]
    {
        $"https://github.com/{repo}/releases/download/{tag}/{file}",
        $"https://ghproxy.net/https://github.com/{repo}/releases/download/{tag}/{file}",
    };

    private static AsrModelFile WhisperFile(string key, string remote, long size) => new()
    {
        LocalPath = $"ggml-{key}.bin",
        Urls = Urls(WhisperRepo, remote),
        SizeBytes = size,
    };

    public static readonly AsrModelInfo[] All =
    {
        // ---------------- Whisper（GGML）----------------
        new()
        {
            Key = "tiny", Family = FamilyWhisper, DisplayName = "Whisper Tiny",
            Note = "最快，准确度较低（适合先试）", Languages = "多语（100 种）",
            HardwareHint = "CPU 即可", SizeMb = 75,
            Files = new[] { WhisperFile("tiny", "ggml-tiny.bin", 75L * 1024 * 1024) },
        },
        new()
        {
            Key = "base", Family = FamilyWhisper, DisplayName = "Whisper Base",
            Note = "推荐：速度与准确度平衡", Languages = "多语（100 种）",
            HardwareHint = "CPU 即可", SizeMb = 142,
            Files = new[] { WhisperFile("base", "ggml-base.bin", 142L * 1024 * 1024) },
        },
        new()
        {
            Key = "small", Family = FamilyWhisper, DisplayName = "Whisper Small",
            Note = "更准，占用更高（日语效果更好）", Languages = "多语（100 种）",
            HardwareHint = "CPU 可用，建议 8 核以上", SizeMb = 466,
            Files = new[] { WhisperFile("small", "ggml-small.bin", 466L * 1024 * 1024) },
        },
        new()
        {
            Key = "turbo", Family = FamilyWhisper, DisplayName = "Whisper Large v3 Turbo",
            Note = "多语质量最好；比 large-v3 快约 8 倍，CPU 上接近可用",
            Languages = "多语（100 种）", HardwareHint = "建议 GPU（Vulkan）或 8 核以上 CPU",
            RecommendGpu = true, SizeMb = 547,
            Files = new[] { WhisperFile("turbo", "ggml-large-v3-turbo-q5_0.bin", 547L * 1024 * 1024) },
        },
        new()
        {
            Key = "anime", Family = FamilyWhisper, DisplayName = "Anime Whisper（暂不推荐）",
            Note = "实测幻听严重、偶尔把日语识别成英文，且体积大/CPU 慢；暂不推荐，优先用 small / turbo",
            Languages = "日语", HardwareHint = "体积大、CPU 很慢；实测效果不佳",
            RecommendGpu = true, SizeMb = 1449,
            Files = new[]
            {
                new AsrModelFile
                {
                    LocalPath = "ggml-anime.bin",
                    Urls = Urls(AnimeRepo, "ggml-litagin-anime-whisper.bin"),
                    SizeBytes = 1449L * 1024 * 1024,
                },
            },
        },

        // ---------------- SenseVoice（ONNX，sherpa-onnx）----------------
        new()
        {
            Key = "sensevoice", Family = FamilySenseVoice, DisplayName = "SenseVoice Small",
            Note = "非自回归、CPU 上约 0.2s/句；中文/英文/韩语/粤语又快又准，日语实测较弱（建议用 Whisper）",
            Languages = "中 / 英 / 韩 / 粤（日语较弱）", HardwareHint = "CPU 即可（推荐）",
            SizeMb = 226,
            Files = new[]
            {
                new AsrModelFile
                {
                    LocalPath = Path.Combine("sensevoice", "model.int8.onnx"),
                    Urls = Urls(SenseVoiceRepo, "model.int8.onnx"),
                    SizeBytes = 226L * 1024 * 1024,
                },
                new AsrModelFile
                {
                    LocalPath = Path.Combine("sensevoice", "tokens.txt"),
                    Urls = Urls(SenseVoiceRepo, "tokens.txt"),
                    SizeBytes = 300L * 1024,
                    IsTokenFile = true,
                },
            },
        },

        // ---------------- 语音增强 / 人声处理（sherpa-onnx speech enhancement）----------------
        new()
        {
            Key = "dpdfnet2", Family = FamilyEnhance, DisplayName = "DPDFNet2 降噪",
            Note = "去除背景音乐/噪声、保留所有人声；实测强 BGM 下识别明显变好（影视推荐）",
            Languages = "—", HardwareHint = "CPU 即可（实测 RTF ≈ 0.13）",
            SizeMb = 10,
            Files = new[]
            {
                new AsrModelFile
                {
                    LocalPath = Path.Combine("enhance", "dpdfnet2.onnx"),
                    Urls = GithubUrls("k2-fsa/sherpa-onnx", "speech-enhancement-models", "dpdfnet2.onnx"),
                    SizeBytes = 10249356,
                },
            },
        },
        new()
        {
            Key = "gtcrn", Family = FamilyEnhance, DisplayName = "GTCRN 降噪（轻量）",
            Note = "0.5MB 轻量降噪，效果弱于 DPDFNet，适合低配设备",
            Languages = "—", HardwareHint = "CPU 即可（实测 RTF ≈ 0.07）",
            SizeMb = 1,
            Files = new[]
            {
                new AsrModelFile
                {
                    LocalPath = Path.Combine("enhance", "gtcrn_simple.onnx"),
                    Urls = GithubUrls("k2-fsa/sherpa-onnx", "speech-enhancement-models", "gtcrn_simple.onnx"),
                    SizeBytes = 535638,
                },
            },
        },
        new()
        {
            Key = "voxlock", Family = FamilyEnhance, DisplayName = "VoxLock 目标说话人提取",
            Note = "只保留锁定到的第一个说话人，其他角色台词会被压制（单人解说/通话适用，多人对话不适用）",
            Languages = "—", HardwareHint = "CPU 可实时（27M 参数因果模型）",
            RecommendGpu = false, SizeMb = 74,
            Files = new[]
            {
                new AsrModelFile
                {
                    LocalPath = Path.Combine("enhance", "voxlock_enroll.onnx"),
                    Urls = GithubUrls("arbazsiddiqui/voxlock", "sherpa-onnx-models-v2", "voxlock_enroll.onnx"),
                    SizeBytes = 21024406,
                },
                new AsrModelFile
                {
                    LocalPath = Path.Combine("enhance", "voxlock_chunk_int8.onnx"),
                    Urls = GithubUrls("arbazsiddiqui/voxlock", "sherpa-onnx-models-v2", "voxlock_chunk_int8.onnx"),
                    SizeBytes = 56561266,
                },
            },
        },
    };

    public static AsrModelInfo? Find(string? key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<AsrModelInfo> Family(string family) =>
        All.Where(m => m.Family == family);

    /// <summary>Whisper 模型选择：auto 按 CPU 核心数（8 核以上 base，否则 tiny）。</summary>
    public static string ResolveWhisperModel(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) &&
            !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // 只接受 whisper 家族里存在的模型名，防止把 "sensevoice" 当 whisper 模型用
            var info = Find(configured);
            return info is { Family: FamilyWhisper } ? info.Key : "base";
        }
        return Environment.ProcessorCount >= 8 ? "base" : "tiny";
    }

    public static string FamilyName(string family) => family switch
    {
        FamilySenseVoice => "SenseVoice（ONNX / sherpa-onnx）",
        FamilyEnhance => "人声处理（降噪 / 目标说话人提取）",
        _ => "Whisper（GGML / Whisper.net）",
    };
}
