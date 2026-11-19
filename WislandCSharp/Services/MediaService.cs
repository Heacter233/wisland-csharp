using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Wisland.Services;

/// <summary>
/// 媒体服务：通过 WinRT SMTC 轮询系统媒体状态（0.7s），媒体键模拟，封面落盘。
/// 对应 Python media.py 的 MediaService。
/// </summary>
public class MediaService : IMediaService, IDisposable
{
    private const double PollSeconds = 0.7;
    private const byte VkMediaNext = 0xB0;
    private const byte VkMediaPrev = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;

    private static readonly string[] DefaultPriority =
        { "cloudmusic", "bilibili", "chrome", "zen", "msedge", "firefox" };

    private static readonly (string Match, string Name)[] Friendly =
    {
        ("cloudmusic", "网易云音乐"), ("netease", "网易云音乐"),
        ("qqmusic", "QQ音乐"), ("bilibili", "哔哩哔哩"),
        ("chrome", "Chrome"), ("msedge", "Edge"), ("zen", "Zen"), ("firefox", "Firefox"),
        ("spotify", "Spotify"), ("potplayer", "PotPlayer"), ("foobar", "foobar2000"),
    };

    private readonly HttpClient _http = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private SynchronizationContext? _ui;
    private MediaState _state = MediaState.Empty;
    private string _lastSig = "";
    private string _coverKey = "";
    private bool _disposed;

    public event EventHandler<MediaState>? Changed;

    public MediaState State { get { lock (_lock) return _state; } }

    public string[] Priority { get; } = DefaultPriority;

    public void Initialize(IntPtr targetHandle)
    {
        _ui = SynchronizationContext.Current;
        _timer ??= new Timer(async _ => await PollAsync(), null, TimeSpan.Zero,
            TimeSpan.FromSeconds(PollSeconds));
    }

    private async Task PollAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = PickSession(_manager);
            _lastSession = session;
            var st = session == null ? MediaState.Empty : await BuildAsync(session);
            Fire(st);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaService] Poll: {ex.Message}");
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions.Count == 0) return null;
        foreach (var app in Priority)
            foreach (var s in sessions)
                if ((s.SourceAppUserModelId ?? "").ToLowerInvariant().Contains(app))
                    return s;
        return manager.GetCurrentSession() ?? sessions[0];
    }

    private async Task<MediaState> BuildAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var appId = session.SourceAppUserModelId ?? "";
        var props = await session.TryGetMediaPropertiesAsync();
        var pb = session.GetPlaybackInfo();
        var tl = session.GetTimelineProperties();
        var playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        double pos = tl.Position.TotalSeconds;
        double dur = tl.EndTime.TotalSeconds;
        if (dur > 0 && pos > dur) pos = dur;

        var st = new MediaState
        {
            HasSession = true,
            AppId = appId,
            AppName = FriendlyName(appId),
            Title = props.Title ?? "",
            Artist = props.Artist ?? "",
            Album = props.AlbumTitle ?? "",
            Playing = playing,
            Position = pos,
            Duration = dur,
            CanPrev = pb.Controls.IsPreviousEnabled,
            CanNext = pb.Controls.IsNextEnabled,
            SongId = $"{props.Title ?? ""}|{props.Artist ?? ""}",
            Stamp = DateTime.UtcNow,
        };

        // 封面：曲目变化时读取一次 SMTC 缩略图并落盘
        var key = st.SongId;
        if (key != _coverKey)
        {
            var path = props.Thumbnail != null ? await SaveThumbnailAsync(props.Thumbnail) : null;
            if (path != null)
            {
                st = Clone(st, path);
                _coverKey = key;
            }
        }
        else if (!string.IsNullOrEmpty(_lastCover))
        {
            st = Clone(st, _lastCover);
        }
        return st;
    }

    private string _lastCover = "";

    private MediaState Clone(MediaState s, string cover) => new()
    {
        HasSession = s.HasSession, AppId = s.AppId, AppName = s.AppName, Title = s.Title,
        Artist = s.Artist, Album = s.Album, Playing = s.Playing, Position = s.Position,
        Duration = s.Duration, CanPrev = s.CanPrev, CanNext = s.CanNext, SongId = s.SongId,
        Stamp = s.Stamp, CoverPath = cover,
    };

    private async Task<string?> SaveThumbnailAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            using var ras = await thumb.OpenReadAsync();
            var size = (int)ras.Size;
            if (size <= 0 || size > 8 * 1024 * 1024) return null;
            var buffer = new Windows.Storage.Streams.Buffer((uint)size);
            await ras.ReadAsync(buffer, (uint)size, InputStreamOptions.None);
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);

            var dir = Path.Combine(AppPaths.DataDir, "covers");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(path, bytes);
            _lastCover = path;
            return path;
        }
        catch { return null; }
    }

    private void Fire(MediaState st)
    {
        var sig = $"{st.AppId}|{st.Title}|{st.Artist}|{st.Playing}|{Math.Round(st.Position)}|{st.CoverPath}";
        MediaState? toFire = null;
        lock (_lock)
        {
            if (sig == _lastSig) return;
            _lastSig = sig;
            _state = st;
            toFire = st;
        }
        if (_ui != null) _ui.Post(_ => Changed?.Invoke(this, toFire!), null);
        else Changed?.Invoke(this, toFire!);
    }

    private static string FriendlyName(string appId)
    {
        var lower = appId.ToLowerInvariant();
        foreach (var (match, name) in Friendly)
            if (lower.Contains(match)) return name;
        var parts = appId.Split('.');
        return parts.Length > 0 && parts[^1].Length > 0 ? parts[^1] : appId;
    }

    public async Task<string?> DownloadCoverAsync(string coverUrl)
    {
        try
        {
            var url = coverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + coverUrl["http://".Length..] : coverUrl;
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;
            var dir = Path.Combine(AppPaths.DataDir, "covers");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch { return null; }
    }

    public void SetPrevious() => SendMediaKey(VkMediaPrev);
    public void SetNext() => SendMediaKey(VkMediaNext);
    public void SetPlayPause() => SendMediaKey(VkMediaPlayPause);

    private GlobalSystemMediaTransportControlsSession? _lastSession;

    /// <summary>跳转到指定位置（通过 SMTC 会话的 TryChangePlaybackPositionAsync）。</summary>
    public void Seek(double seconds)
    {
        var session = _lastSession;
        if (session == null) return;
        var ticks = TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks;
        _ = Task.Run(async () =>
        {
            try { await session.TryChangePlaybackPositionAsync(ticks); }
            catch (Exception ex) { Debug.WriteLine($"[MediaService] Seek: {ex.Message}"); }
        });
    }

    private static void SendMediaKey(byte vk)
    {
        try { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, KeyeventfKeyUp, UIntPtr.Zero); }
        catch (Exception ex) { Debug.WriteLine($"[MediaService] SendMediaKey: {ex.Message}"); }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KeyeventfKeyUp = 0x0002;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _http.Dispose();
    }
}