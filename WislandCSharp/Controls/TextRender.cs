using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Controls;

public enum TextAlign { Left, Center, Right }

/// <summary>绘制文本的辅助（WPF FormattedText 封装，避免每处重复样板）。</summary>
public static class Text
{
    private const double PixelsPerDip = 1.0;

    private static Typeface UiFace(FontWeight weight) => new(
        new FontFamily(Wisland.Theme.Fonts.Ui), FontStyles.Normal, weight, FontStretches.Normal);

    private static Typeface NumFace(FontWeight weight) => new(
        new FontFamily(Wisland.Theme.Fonts.Num), FontStyles.Normal, weight, FontStretches.Normal);

    private static Typeface IconFace => new(
        new FontFamily(Wisland.Theme.Fonts.Icon), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static Typeface SymbolFace => new(
        new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static FormattedText Build(string text, double px, FontWeight weight, Brush brush,
        bool icon = false, bool num = false, bool symbol = false)
    {
        var typeface = symbol ? SymbolFace : (icon ? IconFace : (num ? NumFace(weight) : UiFace(weight)));
        return new FormattedText(
            text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, Math.Max(1, Math.Round(px)), brush, PixelsPerDip);
    }

    public static double Width(string text, double px, FontWeight weight, bool icon = false, bool num = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Build(text, px, weight, Brushes.Transparent, icon, num).WidthIncludingTrailingWhitespace;
    }

    /// <summary>在给定矩形内绘制单行文本（垂直居中）。</summary>
    public static void Draw(DrawingContext dc, string text, double px, FontWeight weight, Color color,
        Rect rect, TextAlign align = TextAlign.Left, bool icon = false, bool num = false, bool symbol = false)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;
        var brush = new SolidColorBrush(color);
        var ft = Build(text, px, weight, brush, icon, num, symbol);
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
        var ft = Build(text, px, weight, new SolidColorBrush(color), icon, num);
        dc.DrawText(ft, new Point(center.X - ft.WidthIncludingTrailingWhitespace / 2, center.Y - ft.Height / 2));
    }
}