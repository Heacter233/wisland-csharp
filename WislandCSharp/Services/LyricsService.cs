using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Wisland.Services;

/// <summary>
/// 歌词（对应 Python services/lyrics.py 的 Lyrics）。
/// </summary>
public sealed class Lyrics
{
    public List<double> Times { get; } = new();
    public List<string> Lines { get; } = new();
    public List<string> Trans { get; } = new();

    /// <summary>
    /// 纯文本歌词（没有时间轴）。此时 Times 为空，
    /// <see cref="At"/> 会按播放进度比例估算当前行，保证界面仍能"滚动"。
    /// </summary>
    public bool Plain { get; init; }

    /// <summary>歌曲总时长（秒），纯文本歌词按比例定位时需要。</summary>
    public double Duration { get; init; }

    /// <summary>返回 position 对应的 (行号, 歌词, 翻译)；未到第一行返回 (-1, "", "")。</summary>
    public (int idx, string line, string trans) At(double pos)
    {
        if (Plain)
        {
            if (Lines.Count == 0) return (-1, "", "");
            var ratio = Duration > 1 ? pos / Duration : 0;
            var i = (int)Math.Floor(ratio * Lines.Count);
            if (i < 0) i = 0;
            if (i >= Lines.Count) i = Lines.Count - 1;
            return (i, Lines[i], "");
        }

        var idx = -1;
        for (var i = 0; i < Times.Count; i++)
        {
            if (Times[i] <= pos) idx = i;
            else break;
        }
        if (idx < 0) return (-1, "", "");
        return (idx, idx < Lines.Count ? Lines[idx] : "", idx < Trans.Count ? Trans[idx] : "");
    }
}

/// <summary>
/// 歌词服务：网易云公开接口（搜索 + 歌词），LRC 解析 + 翻译行合并。
/// 对应 Python services/lyrics.py 的 LyricsService。
/// </summary>
public sealed class LyricsService : IDisposable
{
    private const string Api = "https://music.163.com/api";
    private const string Referer = "https://music.163.com/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    private static readonly Regex TsRe = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex CreditRe = new(@"^\s*[^\s:：]{1,12}\s*[:：]", RegexOptions.Compiled);

    private readonly HttpClient _http = new();

    /// <summary>
    /// 第三方歌词源（LiriQo / LRCLIB）专用 HttpClient。
    /// 两个服务都要求可识别的 User-Agent（LRCLIB 在其 API 文档里明确要求）。
    /// </summary>
    private static readonly HttpClient _lyricsHttp = CreateLyricsHttp();

    private static HttpClient CreateLyricsHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Wisland/0.2 (Windows desktop widget; +https://github.com/Heacter233/wisland-csharp)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }
        catch { }
        return http;
    }

    private readonly Dictionary<string, Lyrics?> _cache = new();
    private readonly HttpClient _api;

    public event EventHandler<string>? Ready;

    public LyricsService()
    {
        _api = new HttpClient();
        _api.DefaultRequestHeaders.Referrer = new Uri(Referer);
        _api.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _api.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _api.Timeout = TimeSpan.FromSeconds(8);
    }

    /// <summary>歌曲缓存键。</summary>
    public static string? Key(MediaState m) =>
        m.Title.Length == 0 ? null : $"{m.Title}|{m.Artist}|{(m.Duration > 0 ? (int)m.Duration : 0)}";

    /// <summary>
    /// 取歌词（命中缓存直接返回）。
    ///
    /// 源优先级：
    ///   1. 网易云 —— 唯一稳定带中文翻译行（tlyric）的源，优先保住双语体验
    ///   2. LiriQo —— 11 源聚合（Apple/Musixmatch/Unison/网易云/酷狗/酷我/咪咕/QQ/LRCLIB/YT Music），
    ///      按质量排序，响应里带原始 LRC，可直接复用 ParseLrc
    ///   3. LRCLIB —— 开放歌词库，同步歌词
    ///   4. 纯文本兜底 —— LRCLIB 的 plainLyrics / LiriQo 的 plain，无时间轴时按进度比例滚动
    /// </summary>
    public async Task<Lyrics?> GetAsync(MediaState m)
    {
        var key = Key(m);
        if (key == null) return null;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Lyrics? result = null;

        // 1) 网易云
        try
        {
            var songId = m.SongId;
            if (!string.IsNullOrEmpty(songId) && songId.All(char.IsDigit))
            {
                result = await FetchLyricsAsync(songId);
            }
            else
            {
                // 网易云 SMTC 一般拿不到数字 id：搜索后依次尝试前几个候选，
                // 因为同名不同版本里常有无歌词的，只试第一个容易错过。
                foreach (var id in await SearchSongIdsAsync(m.Title, m.Artist, m.Duration, 3))
                {
                    result = await FetchLyricsAsync(id);
                    if (result != null) break;
                }
            }
        }
        catch { }

        // 2) LiriQo（聚合源）
        if (result == null) result = await FetchFromLiriQoAsync(m);

        // 3) LRCLIB 同步歌词
        if (result == null) result = await FetchFromLrclibAsync(m, syncedOnly: true);

        // 4) 纯文本兜底：LRCLIB → LiriQo
        if (result == null) result = await FetchFromLrclibAsync(m, syncedOnly: false);
        if (result == null) result = await FetchFromLiriQoAsync(m, plainOnly: true);

        _cache[key] = result;
        Ready?.Invoke(this, key);
        return result;
    }

    // ---------------- LiriQo（多源聚合，公开 API，无需 key） ----------------

    private const string LiriQoApi = "https://api.liriqo-alfarrizi.my.id/v1";

    /// <summary>被限流/不可用时的冷却截止时间，避免每次切歌都去打一个已失效的源。</summary>
    private DateTime _liriqoBlockedUntil = DateTime.MinValue;
    private DateTime _lrclibBlockedUntil = DateTime.MinValue;

    private async Task<Lyrics?> FetchFromLiriQoAsync(MediaState m, bool plainOnly = false)
    {
        if (DateTime.UtcNow < _liriqoBlockedUntil) return null;
        if (string.IsNullOrWhiteSpace(m.Title)) return null;

        try
        {
            var q = new List<string>
            {
                "title=" + Uri.EscapeDataString(m.Title),
            };
            if (!string.IsNullOrWhiteSpace(m.Artist)) q.Add("artist=" + Uri.EscapeDataString(m.Artist));
            if (m.Duration > 1) q.Add("duration=" + ((int)Math.Round(m.Duration)).ToString(CultureInfo.InvariantCulture));
            // fast=1：延迟优先，返回已验证的最佳结果，避免等全部 11 个源
            q.Add("fast=1");

            var url = $"{LiriQoApi}/lyrics?{string.Join("&", q)}";
            using var resp = await _lyricsHttp.GetAsync(url);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _liriqoBlockedUntil = DateTime.UtcNow.AddMinutes(5);
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var track = SelectLiriQoTrack(root, plainOnly);
            if (track == null) return null;

            // 优先用原始 LRC（可直接进 ParseLrc），没有再退回 timed[] 组装
            if (!plainOnly && track.Value.TryGetProperty("lrc", out var lrcEl) &&
                lrcEl.ValueKind == JsonValueKind.String)
            {
                var lyr = ParseLrc(lrcEl.GetString() ?? "");
                if (lyr.Times.Count > 0) return lyr;
            }

            if (!plainOnly && track.Value.TryGetProperty("timed", out var timed) &&
                timed.ValueKind == JsonValueKind.Array && timed.GetArrayLength() > 0)
            {
                var lyr = FromTimedLines(timed);
                if (lyr.Times.Count > 0) return lyr;
            }

            if (track.Value.TryGetProperty("plain", out var plainEl) && plainEl.ValueKind == JsonValueKind.String)
                return FromPlainText(plainEl.GetString() ?? "", m.Duration);

            return null;
        }
        catch { return null; }
    }

    /// <summary>从 LiriQo 响应里挑最合适的 track：优先 primary，其次质量排序后的第一条。</summary>
    private static JsonElement? SelectLiriQoTrack(JsonElement root, bool plainOnly)
    {
        JsonElement? best = null;

        if (root.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
        {
            if (HasLyricPayload(primary, plainOnly)) best = primary;
        }

        if (best == null && root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tracks.EnumerateArray())
            {
                if (!HasLyricPayload(t, plainOnly)) continue;
                best = t;
                break;
            }
        }
        return best;
    }

    private static bool HasLyricPayload(JsonElement t, bool plainOnly)
    {
        if (plainOnly) return t.TryGetProperty("plain", out var p) && p.ValueKind == JsonValueKind.String;
        if (t.TryGetProperty("lrc", out var l) && l.ValueKind == JsonValueKind.String && (l.GetString() ?? "").Length > 0)
            return true;
        if (t.TryGetProperty("timed", out var td) && td.ValueKind == JsonValueKind.Array && td.GetArrayLength() > 0)
            return true;
        return false;
    }

    /// <summary>LiriQo 的 timed[]（start 为毫秒）→ 现有 Lyrics 模型。</summary>
    private static Lyrics FromTimedLines(JsonElement timed)
    {
        var lyr = new Lyrics();
        var pairs = new List<(double T, string S)>();
        foreach (var line in timed.EnumerateArray())
        {
            var text = line.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String
                ? tx.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            // 纯音乐/间奏行只有标记没有文字
            if (line.TryGetProperty("isInstrumental", out var ins) && ins.ValueKind == JsonValueKind.True) continue;
            if (!line.TryGetProperty("start", out var st)) continue;
            var ms = st.ValueKind == JsonValueKind.Number ? st.GetDouble() : 0;
            pairs.Add((ms / 1000.0, text.Trim()));
        }
        foreach (var (t, s) in pairs.OrderBy(p => p.T))
        {
            lyr.Times.Add(t);
            lyr.Lines.Add(s);
            lyr.Trans.Add("");
        }
        return lyr;
    }

    private static Lyrics FromPlainText(string text, double duration)
    {
        var lyr = new Lyrics { Plain = true, Duration = duration > 1 ? duration : 0 };
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            lyr.Lines.Add(line);
            lyr.Trans.Add("");
        }
        return lyr;
    }

    // ---------------- LRCLIB（开放歌词库） ----------------

    private async Task<Lyrics?> FetchFromLrclibAsync(MediaState m, bool syncedOnly)
    {
        if (DateTime.UtcNow < _lrclibBlockedUntil) return null;
        if (string.IsNullOrWhiteSpace(m.Title)) return null;

        try
        {
            var q = new List<string> { "track_name=" + Uri.EscapeDataString(m.Title) };
            if (!string.IsNullOrWhiteSpace(m.Artist)) q.Add("artist_name=" + Uri.EscapeDataString(m.Artist));
            var url = $"https://lrclib.net/api/search?{string.Join("&", q)}";

            using var resp = await _lyricsHttp.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _lrclibBlockedUntil = DateTime.UtcNow.AddMinutes(5);
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            // 按时长接近度 + 是否有同步歌词排序
            var candidates = new List<(double Score, string Synced, string Plain)>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() ?? "" : "";
                var dur = e.TryGetProperty("duration", out var de) && de.ValueKind == JsonValueKind.Number ? de.GetDouble() : 0;
                var synced = e.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String ? sl.GetString() ?? "" : "";
                var plain = e.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString() ?? "" : "";
                if (synced.Length == 0 && plain.Length == 0) continue;

                var score = 0.0;
                if (synced.Length > 0) score -= 50;                       // 同步歌词优先
                if (!string.Equals(name.Trim(), m.Title.Trim(), StringComparison.OrdinalIgnoreCase)) score += 20;
                if (m.Duration > 1 && dur > 1) score += Math.Abs(m.Duration - dur);
                candidates.Add((score, synced, plain));
            }

            if (candidates.Count == 0) return null;

            foreach (var c in candidates.OrderBy(x => x.Score))
            {
                if (syncedOnly)
                {
                    if (c.Synced.Length == 0) continue;
                    var lyr = ParseLrc(c.Synced);
                    if (lyr.Times.Count > 0) return lyr;
                }
                else
                {
                    if (c.Plain.Length == 0) continue;
                    return FromPlainText(c.Plain, m.Duration);
                }
            }
            return null;
        }
        catch { return null; }
    }

    private async Task<List<string>> SearchSongIdsAsync(string title, string artist, double duration, int take = 1)
    {
        try
        {
            var q = Uri.EscapeDataString($"{title} {artist}".Trim());
            var url = $"{Api}/search/get/web?s={q}&type=1&limit=8";
            using var resp = await _api.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return new List<string>();
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("result", out var result)) return new List<string>();
            if (!result.TryGetProperty("songs", out var songs) || songs.GetArrayLength() == 0) return new List<string>();

            // 时长过滤（30s..15min），按名称/时长排序取前 take 个
            var scored = new List<(double Score, string Id)>();
            foreach (var s in songs.EnumerateArray())
            {
                var dur = s.TryGetProperty("duration", out var d) ? d.GetDouble() / 1000.0 : 0;
                if (dur < 30 || dur > 15 * 60) continue;
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var score = Math.Abs(duration - dur);
                if (!string.Equals(name.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)) score += 30;
                if (s.TryGetProperty("id", out var id))
                    scored.Add((score, id.GetInt64().ToString()));
            }
            return scored.OrderBy(x => x.Score).Take(Math.Max(1, take)).Select(x => x.Id).ToList();
        }
        catch { return new List<string>(); }
    }

    private async Task<Lyrics?> FetchLyricsAsync(string songId)
    {
        try
        {
            var url = $"{Api}/song/lyric?id={songId}&lv=1&tv=-1";
            using var resp = await _api.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var main = root.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var ly)
                ? ly.GetString() ?? "" : "";
            var trans = root.TryGetProperty("tlyric", out var tl) && tl.TryGetProperty("lyric", out var ty)
                ? ty.GetString() ?? "" : "";

            var lyr = ParseLrc(main);
            if (lyr.Times.Count == 0) return null;

            // 翻译行按时间戳并入
            var tr = ParseLrc(trans);
            if (tr.Times.Count > 0)
            {
                var map = new Dictionary<string, string>();
                for (var i = 0; i < tr.Times.Count && i < tr.Lines.Count; i++)
                    map[tr.Times[i].ToString("F2", CultureInfo.InvariantCulture)] = tr.Lines[i];
                for (var i = 0; i < lyr.Times.Count; i++)
                {
                    var k = lyr.Times[i].ToString("F2", CultureInfo.InvariantCulture);
                    lyr.Trans.Add(map.TryGetValue(k, out var t) ? t : "");
                }
            }
            else
            {
                for (var i = 0; i < lyr.Times.Count; i++) lyr.Trans.Add("");
            }
            return lyr;
        }
        catch { return null; }
    }

    /// <summary>解析 LRC 文本，过滤制作信息行。</summary>
    public static Lyrics ParseLrc(string text)
    {
        var lyr = new Lyrics();
        if (string.IsNullOrWhiteSpace(text)) return lyr;

        var pairs = new List<(double t, string s)>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var matches = TsRe.Matches(line);
            if (matches.Count == 0)
            {
                // 元信息行（[ti:]/[ar:] 等）已在正则中不匹配则跳过
                continue;
            }
            var remain = TsRe.Replace(line, "").Trim();
            if (remain.Length == 0) continue;
            if (CreditRe.IsMatch(remain)) continue;      // 制作信息
            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var frac = 0.0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    frac = double.Parse("0." + f, CultureInfo.InvariantCulture);
                }
                pairs.Add((min * 60 + sec + frac, remain));
            }
        }

        foreach (var (t, s) in pairs.OrderBy(p => p.t))
        {
            lyr.Times.Add(t);
            lyr.Lines.Add(s);
        }
        return lyr;
    }

    public void Dispose()
    {
        _http.Dispose();
        _api.Dispose();
    }
}
