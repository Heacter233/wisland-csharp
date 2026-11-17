using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Wisland.Theme;

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets 码位（对应 Python ui/theme.py 的 G 类）。</summary>
public static class Glyph
{
    public const string Home = "\uE80F";
    public const string Message = "\uE8BD";
    public const string Mail = "\uE715";
    public const string Sun = "\uE706";
    public const string Cloud = "\uE753";
    public const string Sliders = "\uE9E9";
    public const string Windows = "\uE7C4";
    public const string Cpu = "\uE950";
    public const string Pulse = "\uE9D9";
    public const string Clip = "\uE77F";
    public const string Copy = "\uE8C8";
    public const string Translate = "\uF2B7";
    public const string Volume = "\uE767";
    public const string VolumeLow = "\uE995";
    public const string Mute = "\uE74F";
    public const string Wifi = "\uE701";
    public const string Bt = "\uE702";
    public const string Globe = "\uE774";
    public const string Snip = "\uE924";
    public const string Play = "\uE768";
    public const string Pause = "\uE769";
    public const string Prev = "\uE892";
    public const string Next = "\uE893";
    public const string Pin = "\uE718";
    public const string Unpin = "\uE77A";
    public const string Close = "\uE711";
    public const string Refresh = "\uE72C";
    public const string Power = "\uE7E8";
    public const string Speed = "\uEC4A";
    public const string Bolt = "\uE945";
    public const string Open = "\uE8A7";
    public const string Apps = "\uECA5";
    public const string Gear = "\uE713";
    public const string Image = "\uE91B";
    public const string Check = "\uE73E";
    public const string Bell = "\uEA8F";
    public const string Chat = "\uE8F2";
    public const string Music = "\uE8D6";
    public const string Monitor = "\uE7F4";
    public const string Trash = "\uE74D";
    public const string Up = "\uE74A";
    public const string Down = "\uE74B";
    public const string Add = "\uE710";
    public const string Pointer = "\uE8B0";
    public const string Eye = "\uE890";
    public const string EyeOff = "\uED1A";
    public const string Search = "\uE721";
    public const string Live = "\uE720";
}

/// <summary>字体族（对应 Python UI_FAMILIES / NUM_FAMILIES / ICON_FAMILIES）。</summary>
public static class Fonts
{
    public const string Ui = "HarmonyOS Sans, MiSans, Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI";
    public const string Num = "HarmonyOS Sans, Segoe UI Variable Display, Segoe UI";
    public const string Icon = "Segoe Fluent Icons, Segoe MDL2 Assets";
}

/// <summary>一套主题色板（对应 Python PALETTES 的一项）。</summary>
public sealed class Palette
{
    public bool Dark { get; init; }
    public Color Text { get; init; }
    public double T2 { get; init; }
    public double T3 { get; init; }
    public Color Tint { get; init; }
    public Color? Tint2 { get; init; }
    public Color Card { get; init; }
    public Color Hover { get; init; }
    public Color Pill { get; init; }
    public Color? PillFg { get; init; }
    public Color Track { get; init; }
    public Color Fill { get; init; }
    public Color On { get; init; }
    public Color? OnFg { get; init; }
    public Color Up { get; init; }
    public Color Down { get; init; }
    public Color Rim1 { get; init; }
    public Color Rim2 { get; init; }
    public Color Outline { get; init; }
    public Color MenuBg { get; init; }
    public Color MenuFg { get; init; }
    public Color MenuBorder { get; init; }
    public string Name { get; init; } = "dark";
}

/// <summary>主题：模式切换 + auto 模式按身后亮度自动明暗（带 3 秒滞回）。</summary>
public static class Themes
{
    public static readonly Dictionary<string, Palette> All = new()
    {
        ["dark"] = new Palette
        {
            Name = "dark", Dark = true,
            Text = Rgb(255, 255, 255), T2 = 0.66, T3 = 0.45,
            Tint = Argb(196, 20, 20, 26), Card = Argb(30, 255, 255, 255), Hover = Argb(34, 255, 255, 255),
            Pill = Argb(52, 255, 255, 255), Track = Argb(34, 255, 255, 255), Fill = Argb(235, 255, 255, 255),
            On = Rgb(0x3b, 0x82, 0xf6), Up = Rgb(0xff, 0x5a, 0x52), Down = Rgb(0x35, 0xc9, 0x8e),
            Rim1 = Argb(60, 255, 255, 255), Rim2 = Argb(14, 255, 255, 255), Outline = Argb(70, 0, 0, 0),
            MenuBg = Argb(240, 36, 36, 40), MenuFg = Rgb(255, 255, 255), MenuBorder = Argb(40, 255, 255, 255),
        },
        ["light"] = new Palette
        {
            Name = "light", Dark = false,
            Text = Rgb(16, 18, 24), T2 = 0.74, T3 = 0.56,
            Tint = Argb(212, 240, 242, 247), Card = Argb(170, 255, 255, 255), Hover = Argb(16, 0, 0, 0),
            Pill = Argb(250, 255, 255, 255), Track = Argb(30, 0, 0, 0), Fill = Argb(255, 255, 255, 255),
            On = Rgb(0x2f, 0x6f, 0xe0), Up = Rgb(0xd4, 0x2c, 0x25), Down = Rgb(0x08, 0x87, 0x5a),
            Rim1 = Argb(200, 255, 255, 255), Rim2 = Argb(60, 255, 255, 255), Outline = Argb(40, 0, 0, 0),
            MenuBg = Argb(245, 248, 248, 250), MenuFg = Rgb(20, 22, 28), MenuBorder = Argb(18, 0, 0, 0),
        },
        ["rouge"] = new Palette
        {
            Name = "rouge", Dark = true,
            Text = Rgb(255, 255, 255), T2 = 0.70, T3 = 0.50,
            Tint = Argb(205, 0, 0, 0), Card = Argb(24, 255, 255, 255), Hover = Argb(34, 255, 255, 255),
            Pill = Argb(235, 255, 124, 162), PillFg = Rgb(0, 0, 0),
            Track = Argb(36, 255, 255, 255), Fill = Rgb(255, 124, 162),
            On = Rgb(255, 124, 162), OnFg = Rgb(0, 0, 0),
            Up = Rgb(255, 124, 162), Down = Rgb(0x5f, 0xe0, 0xb0),
            Rim1 = Argb(170, 255, 124, 162), Rim2 = Argb(40, 255, 124, 162), Outline = Argb(140, 0, 0, 0),
            MenuBg = Argb(245, 8, 8, 10), MenuFg = Rgb(255, 255, 255), MenuBorder = Argb(90, 255, 124, 162),
        },
        ["sky"] = new Palette
        {
            Name = "sky", Dark = false,
            Text = Rgb(12, 32, 56), T2 = 0.78, T3 = 0.60,
            Tint = Argb(215, 50, 171, 253), Tint2 = Argb(230, 247, 239, 225),
            Card = Argb(105, 255, 255, 255), Hover = Argb(120, 255, 255, 255),
            Pill = Argb(190, 255, 255, 255), Track = Argb(34, 12, 32, 56), Fill = Argb(250, 255, 255, 255),
            On = Rgb(0x16, 0x67, 0xc9), Up = Rgb(0xd4, 0x2c, 0x25), Down = Rgb(0x06, 0x80, 0x5a),
            Rim1 = Argb(210, 255, 255, 255), Rim2 = Argb(70, 255, 255, 255), Outline = Argb(60, 12, 32, 56),
            MenuBg = Argb(248, 247, 239, 225), MenuFg = Rgb(12, 32, 56), MenuBorder = Argb(70, 50, 171, 253),
        },
    };

    public static readonly (string Mode, string Label)[] Names =
    {
        ("auto", "跟随背景自动"), ("light", "浅色毛玻璃"), ("dark", "深色毛玻璃"),
        ("rouge", "胭脂撞色"), ("sky", "晴空渐变"),
    };

    private static double _lum = 0.5;
    private static DateTime? _flipSince;
    private static string _mode = "dark";

    public static event EventHandler? Changed;

    public static Palette Current => All[CurrentName];

    public static bool IsDark => Current.Dark;

    /// <summary>当前实际生效的色板名（auto 会解析成 dark/light）。</summary>
    public static string CurrentName { get; private set; } = "dark";

    public static string Mode
    {
        get => _mode;
        set { _mode = value; Apply(); }
    }

    private static void Apply()
    {
        var want = value_or(_mode);
        if (want == CurrentName) return;
        CurrentName = want;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string value_or(string mode) =>
        mode is "light" or "dark" or "rouge" or "sky" ? mode : (All["dark"].Dark ? "dark" : "light");

    /// <summary>喂入身后画面平均亮度（0..1）；auto 模式下越过阈值并持续 3 秒才切换。</summary>
    public static void FeedLuminance(double lum)
    {
        _lum = _lum * 0.8 + lum * 0.2;
        if (_mode != "auto") return;

        bool want;
        if (All[CurrentName].Dark && _lum > 0.64) want = false;
        else if (!All[CurrentName].Dark && _lum < 0.42) want = true;
        else { _flipSince = null; return; }

        var wantName = want ? "dark" : "light";
        if (wantName == CurrentName) { _flipSince = null; return; }

        _flipSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _flipSince.Value).TotalSeconds < 3.0) return;
        _flipSince = null;
        if (CurrentName != wantName) { CurrentName = wantName; Changed?.Invoke(null, EventArgs.Empty); }
    }

    // ---------------- 颜色辅助 ----------------
    public static Color Text(double a = 1.0) => WithAlpha(Current.Text, a);
    public static Color Text2(double a = 1.0) => WithAlpha(Current.Text, Current.T2 * a);
    public static Color Text3(double a = 1.0) => WithAlpha(Current.Text, Current.T3 * a);
    public static Color Tint() => Current.Tint;
    public static Color Card(double a = 1.0) => WithAlpha(Current.Card, a);
    public static Color Hover() => Current.Hover;
    public static Color ActivePill() => Current.Pill;
    public static Color Pill() => Current.Pill;
    public static Color CardBorder() => WithAlpha(Current.Text, 0.10);

    /// <summary>滚动区底板：接近不透明，用于消除滚动残影。</summary>
    public static Color ScrollPlate()
    {
        var tint = Current.Tint;
        // 与玻璃同色系，但把不透明度拉高
        var alpha = (byte)Math.Clamp(tint.A + 60, 200, 250);
        return Color.FromArgb(alpha, tint.R, tint.G, tint.B);
    }
    public static Color PillText(double a = 1.0) => Current.PillFg is { } fg ? WithAlpha(fg, a) : Text(a);
    public static Color Track() => Current.Track;
    public static Color Fill() => Current.Fill;
    public static Color OnColor() => Current.On;
    public static Color OnFg(double a = 1.0) => WithAlpha(Current.OnFg ?? Colors.White, a);
    public static Color RimTop() => Current.Rim1;
    public static Color RimBottom() => Current.Rim2;
    public static Color Outline() => Current.Outline;

    /// <summary>玻璃色调画刷：渐变主题（sky）用纵向渐变，其余纯色。</summary>
    public static Brush TintBrush(double width, double height)
    {
        var p = Current;
        if (p.Tint2 is not { } t2) return new SolidColorBrush(p.Tint);
        var g = new LinearGradientBrush(p.Tint, t2, new System.Windows.Point(0, 0), new System.Windows.Point(0.25, 1));
        return g;
    }

    public static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Clamp(c.A * a, 0, 255), c.R, c.G, c.B);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}

/// <summary>消息类型配色（对应 Python KIND_COLORS）。</summary>
public static class KindColors
{
    public static Color Qq => (Color)ColorConverter.ConvertFromString("#12b7f5");
    public static Color Wechat => (Color)ColorConverter.ConvertFromString("#07c160");
    public static Color Mail => (Color)ColorConverter.ConvertFromString("#ff9f0a");
    public static Color Other => (Color)ColorConverter.ConvertFromString("#8e8e93");

    public static Color Get(string kind) => kind switch
    {
        "qq" => Qq, "wechat" => Wechat, "mail" => Mail, _ => Other,
    };
}