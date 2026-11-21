using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Controls;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// 绘制文本的辅助（WPF FormattedText 封装，避免每处重复样板）。
///
/// 性能注意：这个类会被自绘控件在**每一帧**调用几十次。
/// 早期实现在每次调用里 new FontFamily / new Typeface / new SolidColorBrush，
/// 一台 60fps 的常驻挂件因此每帧产生上百个短命对象（GC 尖峰 → 掉帧）。
/// 现在：
///   - Typeface 按 (字体族, 字重, 数字/图标/符号) 缓存；
///   - 颜色画刷按 ARGB 缓存并 Freeze（可跨帧、跨控件安全共享）；
///   - 字宽测量结果也做小缓存（NavBar 布局会反复量同一批标签）；
///   - PixelsPerDip 由宿主在启动/DPI 变化时写入，高 DPI 下文字不再发虚。
/// </summary>
public static class Text
{
    private static double _pixelsPerDip = 1.0;

    /// <summary>文本渲染的 DPI 缩放（1.0 = 96 DPI）。宿主应在 DPI 变化时更新。</summary>
    public static double PixelsPerDip
    {
        get => _pixelsPerDip;
        set => _pixelsPerDip = value > 0 ? value : 1.0;
    }

    private const int MaxBrushes = 256;
    private const int MaxWidths = 512;

    private static readonly Dictionary<(string Family, int Weight, int Kind), Typeface> Faces = new();

    private static readonly Dictionary<uint, Brush> Brushes = new();
    private static readonly object BrushLock = new();

    private static readonly Dictionary<(string Text, int Px, int Weight, int Kind), double> Widths = new();

    // Kind：0 = UI 字体，1 = 数字字体，2 = 图标字体，3 = 系统符号
    private static Typeface Face(FontWeight weight, bool icon, bool num, bool symbol)
    {
        var kind = symbol ? 3 : icon ? 2 : num ? 1 : 0;
        var family = kind == 3
            ? "Segoe UI Symbol"
            : kind == 2 ? Wisland.Theme.Fonts.Icon
            : kind == 1 ? Wisland.Theme.Fonts.Num
            : Wisland.Theme.Fonts.Ui;
        var weightValue = weight.ToOpenTypeWeight();

        var key = (Family: family, Weight: weightValue, Kind: kind);
        if (Faces.TryGetValue(key, out var cached)) return cached;

        var face = new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal);
        Faces[key] = face;
        return face;
    }

    /// <summary>按颜色取（冻结的）画刷；颜色离散（透明度动画）时用容量上限自清。</summary>
    private static Brush BrushFor(Color color)
    {
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        lock (BrushLock)
        {
            if (Brushes.TryGetValue(key, out var cached)) return cached;

            if (Brushes.Count >= MaxBrushes) Brushes.Clear();   // 透明度连续变化时避免无界增长
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[key] = brush;
            return brush;
        }
    }

    public static FormattedText Build(string text, double px, FontWeight weight, Brush brush,
        bool icon = false, bool num = false, bool symbol = false)
    {
        return new FormattedText(
            text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face(weight, icon, num, symbol), Math.Max(1, Math.Round(px)), brush, _pixelsPerDip);
    }

    public static double Width(string text, double px, FontWeight weight, bool icon = false, bool num = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var kind = icon ? 2 : num ? 1 : 0;
        var key = (text, (int)Math.Round(px), weight.ToOpenTypeWeight(), kind);
        if (Widths.TryGetValue(key, out var cached)) return cached;

        var w = Build(text, px, weight, System.Windows.Media.Brushes.Transparent, icon, num)
            .WidthIncludingTrailingWhitespace;
        if (Widths.Count >= MaxWidths) Widths.Clear();
        Widths[key] = w;
        return w;
    }

    /// <summary>在给定矩形内绘制单行文本（垂直居中）。</summary>
    public static void Draw(DrawingContext dc, string text, double px, FontWeight weight, Color color,
        Rect rect, TextAlign align = TextAlign.Left, bool icon = false, bool num = false, bool symbol = false)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;
        var ft = Build(text, px, weight, BrushFor(color), icon, num, symbol);
        double x = align switch
        {
            TextAlign.Center => rect.X + (rect.Width - ft.WidthIncludingTrailingWhitespace) / 2,
            TextAlign.Right => rect.X + rect.Width - ft.WidthIncludingTrailingWhitespace,
            _ => rect.X,
        };
        var y = rect.Y + (rect.Height - ft.Height) / 2;
        dc.DrawText(ft, new Point(x, y));
    }

    /// <summary>以中心点绘制。</summary>
    public static void DrawCenter(DrawingContext dc, string text, double px, FontWeight weight, Color color,
        Point center, bool icon = false, bool num = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = Build(text, px, weight, BrushFor(color), icon, num);
        dc.DrawText(ft, new Point(center.X - ft.WidthIncludingTrailingWhitespace / 2, center.Y - ft.Height / 2));
    }
}
