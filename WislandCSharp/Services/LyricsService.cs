using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    /// <summary>返回 position 对应的 (行号, 歌词, 翻译)；未到第一行返回 (-1, "", "")。</summary>
    public (int idx, string line, string trans) At(double pos)
    {
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

    /// <summary>取歌词（命中缓存直接返回）。</summary>
    public async Task<Lyrics?> GetAsync(MediaState m)
    {
        var key = Key(m);
        if (key == null) return null;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Lyrics? result = null;
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

        _cache[key] = result;
        Ready?.Invoke(this, key);
        return result;
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
