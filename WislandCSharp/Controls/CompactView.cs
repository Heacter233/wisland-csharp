using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Controls;

/// <summary>临时活动提示（音量/亮度/翻译/通知等）。</summary>
public sealed class Activity
{
    public string Type { get; init; } = "";
    public string Glyph { get; init; } = Glyph2.Bell;
    public Color? Color { get; init; }
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public double Value { get; init; }
    public bool Muted { get; init; }
    public DateTime Start { get; } = DateTime.UtcNow;
    public DateTime Until { get; set; } = DateTime.UtcNow.AddSeconds(2.4);

    public Activity With(Action<ActivityBuilder> mutate) => this; // 保留链式占位

    public void Update(Action<Activity> mutate) => mutate(this);
}

public sealed class ActivityBuilder
{
    public string? Title { get; set; }
    public string? Sub { get; set; }
    public double? Value { get; set; }
    public bool? Muted { get; set; }
}

/// <summary>Glyph 别名（避免与 System.Windows.Controls.Glyph 之类冲突）。</summary>
internal static class Glyph2
{
    public const string Bell = Wisland.Theme.Glyph.Bell;
}

/// <summary>
/// 收起态内容绘制（对应 Python ui/compact.py 的 CompactView）。
/// 三种视图：clock（时间+天气）/ pet（小形象）/ music（媒体），优先级：活动 &gt; 字幕 &gt; 视图。
/// </summary>
public sealed class CompactView : FrameworkElement
{
    public const double HIdle = 34;
    public const double HMedia = 36;
    public const double HAct = 40;
    public const double HSub = 60;          // 单行原文+译文的最小高度
    public const double HSubMin = 64;       // 收起态字幕最小高度
    public const double HSubMax = 112;      // 最多 4 行（原文 2 + 译文 2）
    public const double WSubMin = 640;      // 收起态字幕最小宽度
    public const double WSubMax = 760;      // 最大宽度
    public const double WText = 640;
    /// <summary>字幕停留秒数；0 = 一直显示到停止聆听（旧行为是固定 7 秒后消失）。</summary>
    public double SubHold { get; set; } = 0;

    private const string Views = "clock,pet,music";

    public MediaState Media { get; set; } = MediaState.Empty;
    public WeatherState Weather { get; set; } = WeatherState.Empty;
    public Dictionary<string, int> Badges { get; } = new();
    public Activity? Act { get; set; }
    public string View { get; set; } = "clock";
    public Lyrics? Lyrics { get; set; }

    public bool SubOn { get; set; }
    public string SubJp { get; set; } = "";
    public string SubZh { get; set; } = "";
    private DateTime _subT = DateTime.UtcNow.AddDays(-1);

    public double Peak { get; set; }
    public bool Near { get; set; }
    public double LookX { get; set; }
    public double LookY { get; set; }
    public PetFace Pet { get; } = new();

    /// <summary>按压形变（0..1.2）与沿轴相对位置（-1..1）。</summary>
    public double Press { get; set; }
    public double PressRel { get; set; }
    public (double X, double Y) Squash { get; set; } = (1.0, 1.0);

    /// <summary>播放/暂停按钮矩形（由绘制更新，供点击判定）。</summary>
    public Rect? PlayRect { get; private set; }
    public double PlayHover { get; set; }

    private readonly double[] _bars = new double[4];
    private double _peakSmooth;
    private string _mode = "idle";
    private DateTime _modeT = DateTime.UtcNow;
    private string _lyrCur = "";
    private string _lyrPrev = "";
    private int _lyrIdx = -2;
    private DateTime _lyrT = DateTime.MinValue;
    private BitmapImage? _coverBmp;
    private string _coverLoaded = "";

    public string Mode => ComputeMode();

    private string ComputeMode()
    {
        var now = DateTime.UtcNow;
        string m;
        if (Act != null && now < Act.Until) m = "activity";
        else if (SubActive())
        {
            m = "subtitle";
            Act = null;
        }
        else
        {
            Act = null;
            m = View switch { "music" => "media", "pet" => "pet", _ => "idle" };
        }
        if (m != _mode)
        {
            _mode = m;
            _modeT = now;
            try { Wisland.App.DebugNote($"[Compact] mode={m} SubOn={SubOn} jp={(SubJp.Length > 20 ? SubJp[..20] : SubJp)} zh={(SubZh.Length > 20 ? SubZh[..20] : SubZh)} hold={SubHold}"); } catch { }
        }
        return m;
    }

    private bool SubActive() => SubOn && (SubJp.Length > 0 || SubZh.Length > 0) &&
                                (SubHold <= 0 || (DateTime.UtcNow - _subT).TotalSeconds < SubHold);

    public void SetSub(string? jp = null, string? zh = null)
    {
        if (jp != null) SubJp = jp;
        if (zh != null) SubZh = zh;
        _subT = DateTime.UtcNow;
    }

    public void SetSubRunning(bool on)
    {
        SubOn = on;
        if (!on) { SubJp = SubZh = ""; _subT = DateTime.UtcNow.AddDays(-1); }
    }

    public bool Playing => Media.HasSession && Media.Playing && Media.Title.Length > 0;

    public bool Busy => ComputeMode() is "media" or "activity" or "pet" or "subtitle";

    public void ShowActivity(Activity act)
    {
        Act = act;
        act.Until = DateTime.UtcNow.AddSeconds(2.4);
    }

    public void ShowActivity(Activity act, double duration)
    {
        Act = act;
        act.Until = DateTime.UtcNow.AddSeconds(duration);
    }

    /// <summary>直接切到指定视图（clock / pet / music）。</summary>
    public void ForceView(string view)
    {
        if (view is "clock" or "pet" or "music") View = view;
        InvalidateVisual();
    }

    public string CycleView(int d)
    {
        var list = Views.Split(',');
        var i = Math.Max(0, Array.IndexOf(list, View));
        View = list[(i + (d < 0 ? 1 : -1) + list.Length) % list.Length];
        return View;
    }

    // ---------------- 收起态字幕布局 ----------------

    private static string OneLine(string? text) =>
        string.Join(" ", (text ?? "").Split(new[] { ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries));

    /// <summary>在给定宽度内测量文本换行后的高度（行数上限 2，即原文/译文各最多 2 行）。</summary>
    private static double WrappedHeight(string text, double px, FontWeight weight, double maxWidth)
    {
        var t = OneLine(text);
        if (t.Length == 0) return 0;
        var ft = Text.Build(t, px, weight, Brushes.Transparent);
        ft.MaxTextWidth = Math.Max(10, maxWidth);
        var lineH = Math.Max(px + 4, ft.Height);
        var lines = Math.Max(1, (int)Math.Round(ft.Height / lineH));
        lines = Math.Min(2, lines);
        return lines * lineH;
    }

    /// <summary>收起态字幕卡片的宽高：宽度随文本增长（640~760），高度按行数增长（64~112）。</summary>
    private (double W, double H) SubSize()
    {
        var jp = OneLine(SubJp);
        var zh = OneLine(SubZh);
        if (zh.Length == 0) zh = "…";

        var textW = Math.Max(
            Text.Width(jp, 12, FontWeights.Normal),
            Text.Width(zh, 15, FontWeights.DemiBold));
        var w = Anim.Clamp(textW + 52 + (BadgeCount() > 0 ? BadgeWidth : 0), WSubMin, WSubMax);

        var inner = Math.Max(80, w - 52 - (BadgeCount() > 0 ? BadgeWidth : 0));
        var jpH = WrappedHeight(jp, 12, FontWeights.Normal, inner);
        var zhH = WrappedHeight(zh, 15, FontWeights.DemiBold, inner);
        var h = 12 + Math.Max(14, jpH) + 6 + zhH + 12;
        return (w, Math.Clamp(h, HSubMin, HSubMax));
    }

    /// <summary>绘制一个自动换行的文本块（居中）。</summary>
    private static void DrawWrapped(DrawingContext dc, string text, double px, FontWeight weight,
        Color color, double x, double y, double maxWidth, double maxHeight)
    {
        var t = OneLine(text);
        if (t.Length == 0 || maxWidth <= 0 || maxHeight <= 0) return;
        var ft = Text.Build(t, px, weight, new SolidColorBrush(color));
        ft.MaxTextWidth = Math.Max(10, maxWidth);
        ft.MaxTextHeight = Math.Max(px + 2, maxHeight);
        ft.TextAlignment = TextAlignment.Center;
        dc.DrawText(ft, new Point(x, y));
    }

    // ---------------- 尺寸 ----------------
    public (double W, double H) SizeHint()
    {
        var m = ComputeMode();
        var nb = 0;
        foreach (var v in Badges.Values) if (v > 0) nb++;

        switch (m)
        {
            case "activity" when Act != null:
                if (Act.Type is "volume" or "brightness") return (280, HAct);
                var w = 70 + Text.Width(Act.Title, 13, FontWeights.DemiBold)
                            + Text.Width(Act.Sub, 12, FontWeights.Normal);
                return (Anim.Clamp(w + 24, 220, 460), HAct);
            case "subtitle":
                var (sw, sh) = SubSize();
                return (sw + nb * 12, sh);
            case "media":
                if (!Playing && Media.Title.Length == 0) return (190 + nb * 12, HMedia);
                return (Math.Max(300, LyricsWidth) + 34 + nb * 12, HMedia);
            case "pet":
                return (150 + nb * 14, HIdle);
            default:
                return (150 + (Weather.Ok ? 64 : 0) + nb * 14, HIdle);
        }
    }

    public double LyricsWidth { get; private set; }

    public void SetLyrics(Lyrics? lyr)
    {
        Lyrics = lyr;
        _lyrIdx = -2;
        _lyrPrev = "";
        if (lyr != null && lyr.Lines.Count > 0)
        {
            var widest = 0.0;
            foreach (var line in lyr.Lines) widest = Math.Max(widest, Text.Width(line, 13, FontWeights.Medium));
            LyricsWidth = Anim.Clamp(widest + 160, WText, 760);
        }
        else LyricsWidth = 0;
    }

    public (int idx, string line, string trans) LyricNow()
    {
        if (Lyrics == null || Media.Title.Length == 0) return (-1, "", "");
        return Lyrics.At(Media.PositionNow);
    }

    // ---------------- 绘制 ----------------
    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        if (r.Width < 40 || r.Height < 10) return;
        Paint(dc, r, 1.0);
    }

    public void Paint(DrawingContext dc, Rect r, double alpha)
    {
        var m = ComputeMode();
        var fade = Anim.EaseOut((DateTime.UtcNow - _modeT).TotalSeconds / 0.28);
        var a = alpha * fade;
        _peakSmooth = _peakSmooth * 0.6 + Peak * 0.4;
        if (m != "media") PlayRect = null;

        dc.PushClip(new RectangleGeometry(r));
        switch (m)
        {
            case "activity": PaintActivity(dc, r, a); break;
            case "subtitle": PaintSubtitle(dc, r, a); break;
            case "media":
                if (Media.Title.Length == 0) PaintNoMedia(dc, r, a);
                else PaintMedia(dc, r, a);
                break;
            case "pet": PaintPet(dc, r, a); break;
            default: PaintIdle(dc, r, a); break;
        }
        dc.Pop();
    }

    private int BadgeCount()
    {
        var n = 0;
        foreach (var v in Badges.Values) if (v > 0) n++;
        return n;
    }

    private double BadgeWidth => 12 + 12 * BadgeCount();

    private double DrawBadges(DrawingContext dc, double right, double cy, double a)
    {
        var x = right;
        foreach (var kind in new[] { "mail", "wechat", "qq" })
        {
            if (!Badges.TryGetValue(kind, out var n) || n <= 0) continue;
            var c = KindColors.Get(kind);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * a), c.R, c.G, c.B));
            dc.DrawEllipse(brush, null, new Point(x - 4, cy), 4, 4);
            x -= 13;
        }
        return x;
    }

    private void PaintSubtitle(DrawingContext dc, Rect r, double a)
    {
        if (BadgeCount() > 0) DrawBadges(dc, r.Right - 14, r.Y + 16, a);

        var x = r.X + 18;
        var w = r.Width - 36 - (BadgeCount() > 0 ? BadgeWidth : 0);
        if (w < 60) return;

        var jp = OneLine(SubJp);
        var zh = OneLine(SubZh);
        if (zh.Length == 0) zh = "…";

        var inner = Math.Max(60, w);
        var jpLineH = 16.0;
        var zhLineH = 20.0;
        var jpH = Math.Min(2 * jpLineH, Math.Max(jpLineH, WrappedHeight(jp, 12, FontWeights.Normal, inner)));
        var zhH = Math.Min(2 * zhLineH, Math.Max(zhLineH, WrappedHeight(zh, 15, FontWeights.DemiBold, inner)));
        var total = jpH + 6 + zhH;

        var y = r.Y + (r.Height - total) / 2;
        DrawWrapped(dc, jp, 12, FontWeights.Normal, Themes.Text(0.62 * a), x, y, inner, jpH);
        DrawWrapped(dc, zh, 15, FontWeights.DemiBold, Themes.Text(a), x, y + jpH + 6, inner, zhH);
    }

    private void PaintNoMedia(DrawingContext dc, Rect r, double a)
    {
        Text.Draw(dc, Glyph.Music, 14, FontWeights.Normal, Themes.Text(0.75 * a),
            new Rect(r.X + 16, r.Y, 22, r.Height), TextAlign.Center, icon: true);
        Text.Draw(dc, "未在播放", 13, FontWeights.Medium, Themes.Text2(a),
            new Rect(r.X + 46, r.Y, r.Width - 60, r.Height), TextAlign.Left);
    }

    private void PaintPet(DrawingContext dc, Rect r, double a)
    {
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        var (sx, sy) = Squash;
        var amount = Anim.Clamp(Press, 0, 1.2);
        var sideW = Math.Abs(PressRel);
        var lean = -PressRel * Math.Min(1.0, amount * 1.6);

        Pet.Spread = amount * (1 - sideW) * 9.0;
        Pet.Tilt = amount * (1 - sideW) * 14.0;

        dc.PushTransform(new TranslateTransform(cx + lean * 24.0, cy));
        dc.PushTransform(new RotateTransform(lean * 18.0));
        dc.PushTransform(new ScaleTransform(sx, sy));
        dc.PushTransform(new TranslateTransform(-cx, -cy));
        Pet.Paint(dc, cx, cy - 1, 1.0, a, LookX, LookY, Near);
        dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();

        DrawBadges(dc, r.Right - 14, cy, a);
    }

    private void PaintIdle(DrawingContext dc, Rect r, double a)
    {
        var cy = r.Y + r.Height / 2;
        Text.Draw(dc, DateTime.Now.ToString("HH:mm"), 15, FontWeights.DemiBold, Themes.Text(a),
            new Rect(r.X + 18, r.Y, 80, r.Height), TextAlign.Left, num: true);

        var x = r.Right - 16;
        x = DrawBadges(dc, x, cy, a) - 4;
        if (Weather.Ok)
        {
            var temp = Weather.TempText;
            var tw = Text.Width(temp, 13, FontWeights.Medium, num: true);
            Text.Draw(dc, temp, 13, FontWeights.Medium, Themes.Text2(a),
                new Rect(x - tw, r.Y, tw + 2, r.Height), TextAlign.Left, num: true);
            Text.Draw(dc, WeatherGlyphChar(Weather.Icon), 13, FontWeights.Normal, Themes.Text2(a),
                new Rect(x - tw - 22, r.Y, 20, r.Height), TextAlign.Center, symbol: true);
        }
    }

    /// <summary>天气图标：Segoe UI Symbol 的天气符号（对应 Python weather_glyph）。</summary>
    public static string WeatherGlyphChar(string icon)
    {
        if (icon.StartsWith("clear")) return "\u2600";           // ☀
        if (icon.StartsWith("mainly")) return "\u2600";
        if (icon.StartsWith("partly")) return "\u26C5";          // ⛅
        if (icon.StartsWith("overcast")) return "\u2601";        // ☁
        if (icon.StartsWith("fog")) return "\u2592";             // ▒
        if (icon.StartsWith("drizzle")) return "\uD83C\uDF26";   // 🌦
        if (icon.StartsWith("rain_heavy")) return "\uD83C\uDF27";// 🌧
        if (icon.StartsWith("rain")) return "\uD83C\uDF26";
        if (icon.StartsWith("freezing")) return "\u2744";        // ❄
        if (icon.StartsWith("snow")) return "\u2744";
        if (icon.StartsWith("showers")) return "\uD83C\uDF26";
        if (icon.StartsWith("thunder")) return "\u26C8";         // ⛈
        return "\u2600";
    }

    private void PaintMedia(DrawingContext dc, Rect r, double a)
    {
        var cy = r.Y + r.Height / 2;

        // 封面（圆角 7）
        var cr = new Rect(r.X + 8, cy - 13, 26, 26);
        var bmp = CoverBitmap();
        dc.PushOpacity(a);
        if (bmp != null)
        {
            dc.PushClip(new RectangleGeometry(cr, 7, 7));
            dc.DrawImage(bmp, cr);
            dc.Pop();
        }
        else
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Themes.Card(1.4)), null, cr, 7, 7);
            Text.Draw(dc, Glyph.Music, 13, FontWeights.Normal, Themes.Text(0.8),
                cr, TextAlign.Center, icon: true);
        }
        dc.Pop();

        // 右侧：播放/暂停键
        var right = BadgeCount() > 0 ? DrawBadges(dc, r.Right - 14, cy, a) - 4 : r.Right - 8;
        var pr = new Rect(right - 28, cy - 14, 28, 28);
        PlayRect = pr;
        if (PlayHover > 0.01)
        {
            dc.PushOpacity(a * PlayHover);
            dc.DrawEllipse(new SolidColorBrush(Themes.Hover()), null,
                new Point(pr.X + pr.Width / 2, pr.Y + pr.Height / 2), 13, 13);
            dc.Pop();
        }
        Text.Draw(dc, Media.Playing ? Glyph.Pause : Glyph.Play, 14, FontWeights.Normal,
            Themes.Text(a), pr, TextAlign.Center, icon: true);

        // 音频律动条
        const double barsW = 26;
        var bx = pr.X - 6 - barsW;
        var t = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        var pk = Anim.Clamp(_peakSmooth * 1.6, 0, 1);
        for (var i = 0; i < 4; i++)
        {
            var target = 0.18 + pk * (0.55 + 0.45 * Math.Pow(Math.Sin(t * (7.3 + i * 2.1) + i * 1.7), 2));
            _bars[i] += (target - _bars[i]) * 0.35;
            var h = 4 + 14 * _bars[i];
            var br = new Rect(bx + i * 7, cy - h / 2, 3.6, h);
            dc.DrawRoundedRectangle(new SolidColorBrush(Themes.Text(0.85 * a)), null, br, 1.8, 1.8);
        }

        // 中间：歌词 / 标题 · 歌手
        var tx = cr.Right + 10;
        var tw = bx - 10 - tx;
        if (tw < 20) return;

        var title = Media.Title;
        var artist = Media.Artist;
        var text = artist.Length > 0 ? $"{title}  ·  {artist}" : title;
        var (idx, line, _) = LyricNow();
        if (line.Length > 0) text = line;

        if (idx != _lyrIdx)
        {
            _lyrPrev = _lyrIdx != -2 ? _lyrCur : "";
            _lyrIdx = idx;
            _lyrT = DateTime.UtcNow;
        }
        _lyrCur = text;

        var k = Anim.EaseOut((DateTime.UtcNow - _lyrT).TotalSeconds / 0.32);
        var full = Text.Width(text, 13, FontWeights.Medium);
        dc.PushClip(new RectangleGeometry(new Rect(tx, r.Y, tw, r.Height)));
        if (k < 1 && _lyrPrev.Length > 0)
        {
            Text.Draw(dc, _lyrPrev, 13, FontWeights.Medium, Themes.Text(a * (1 - k)),
                new Rect(tx, r.Y - 9 * k, tw, r.Height), line.Length > 0 ? TextAlign.Center : TextAlign.Left);
        }
        dc.PushTransform(new TranslateTransform(0, _lyrPrev.Length > 0 ? 9 * (1 - k) : 0));
        var alphaK = _lyrPrev.Length > 0 ? k : 1.0;
        if (full <= tw)
        {
            Text.Draw(dc, text, 13, FontWeights.Medium, Themes.Text(a * alphaK),
                new Rect(tx, r.Y, tw, r.Height), line.Length > 0 ? TextAlign.Center : TextAlign.Left);
        }
        else
        {
            // 过长：34px/s 循环滚动（停顿 2s）
            var gap = 40.0;
            var period = (full + gap) / 34.0;
            var tt = (DateTime.UtcNow - _lyrT).TotalSeconds;
            var ph = tt % (period + 2.0);
            var off = ph < 2.0 ? 0 : (ph - 2.0) * 34.0;
            for (var kk = 0; kk < 2; kk++)
            {
                Text.Draw(dc, text, 13, FontWeights.Medium, Themes.Text(a * alphaK),
                    new Rect(tx - off + kk * (full + gap), r.Y, full + 4, r.Height), TextAlign.Left);
            }
        }
        dc.Pop();
        dc.Pop();
    }

    private void PaintActivity(DrawingContext dc, Rect r, double a)
    {
        var act = Act;
        if (act == null) return;
        var cy = r.Y + r.Height / 2;
        var enter = Anim.EaseOut((DateTime.UtcNow - act.Start).TotalSeconds / 0.35);

        // 左侧图标圆（26）
        var d = 26.0;
        var cr = new Rect(r.X + 9, cy - d / 2, d, d);
        if (act.Color is { } col)
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * a), col.R, col.G, col.B)), null,
                new Point(cr.X + cr.Width / 2, cr.Y + cr.Height / 2), d / 2, d / 2);
            Text.Draw(dc, act.Glyph, 13, FontWeights.Normal, Colors.White, cr, TextAlign.Center, icon: true);
        }
        else
        {
            Text.Draw(dc, act.Glyph, 16, FontWeights.Normal, Themes.Text(a), cr, TextAlign.Center, icon: true);
        }

        var x = cr.Right + 10;
        if (act.Type is "volume" or "brightness")
        {
            var v = Anim.Clamp(act.Value, 0, 1);
            var bw = r.Right - 52 - x;
            var track = new Rect(x, cy - 3, bw, 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Themes.Track()), null, track, 3, 3);
            dc.DrawRoundedRectangle(new SolidColorBrush(Themes.Text(a)), null,
                new Rect(x, cy - 3, Math.Max(6, bw * v * enter), 6), 3, 3);
            Text.Draw(dc, act.Muted ? "静音" : $"{Math.Round(v * 100)}", 12.5, FontWeights.DemiBold,
                Themes.Text(a), new Rect(r.Right - 48, r.Y, 34, r.Height), TextAlign.Right, num: true);
            return;
        }

        var avail = r.Right - 14 - x;
        var tw = Text.Width(act.Title, 13, FontWeights.DemiBold);
        Text.Draw(dc, act.Title, 13, FontWeights.DemiBold, Themes.Text(a),
            new Rect(x, r.Y, Math.Min(tw + 2, avail), r.Height), TextAlign.Left);
        if (act.Sub.Length > 0 && tw + 10 < avail)
        {
            Text.Draw(dc, act.Sub, 12, FontWeights.Normal, Themes.Text2(a),
                new Rect(x + tw + 10, r.Y, avail - tw - 10, r.Height), TextAlign.Left);
        }
    }

    private BitmapImage? CoverBitmap()
    {
        var path = Media.CoverPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (_coverLoaded == path) return _coverBmp;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            _coverBmp = bmp;
            _coverLoaded = path;
            return bmp;
        }
        catch { return null; }
    }
}