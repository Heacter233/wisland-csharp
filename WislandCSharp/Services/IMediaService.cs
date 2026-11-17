using System;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>媒体服务接口：SMTC 状态 + 控制（对应 Python media.py 的 MediaService）。</summary>
public interface IMediaService
{
    MediaState State { get; }

    /// <summary>状态变化（已回到 UI 线程）。</summary>
    event EventHandler<MediaState>? Changed;

    /// <summary>应用优先级（config.json → media.priority）。</summary>
    string[] Priority { get; }

    /// <summary>初始化并开始轮询。</summary>
    void Initialize(IntPtr targetHandle);

    /// <summary>下载网络封面到本地，返回路径。</summary>
    Task<string?> DownloadCoverAsync(string coverUrl);

    void SetPrevious();
    void SetNext();
    void SetPlayPause();

    /// <summary>跳转到指定播放位置（秒）。</summary>
    void Seek(double seconds);
}