using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Controls;

/// <summary>主页上的小形象（对应 Python ui/pages/home.py 的 PetWidget）。</summary>
public sealed class PetWidget : FrameworkElement
{
    public PetFace Pet { get; } = new();
    private Point _mouse = new(double.NaN, double.NaN);

    public PetWidget()
    {
        IsHitTestVisible = true;
        Loaded += (_, _) => { };
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = new Point(double.NaN, double.NaN);
        InvalidateVisual();
        base.OnMouseLeave(e);
    }

    public void React(string kind = "surprise")
    {
        Pet.React(kind);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;

        var cx = w / 2;
        var cy = h / 2;
        var s = Math.Min(2.1, Math.Min(w, h) / 78.0);

        double lx = 0, ly = 0;
        var near = false;
        if (!double.IsNaN(_mouse.X))
        {
            lx = _mouse.X - cx;
            ly = _mouse.Y - cy;
            near = Math.Abs(lx) < w / 2 && Math.Abs(ly) < h / 2;
        }

        // 柔光底
        var glow = new RadialGradientBrush(
            Color.FromArgb(28, Themes.OnColor().R, Themes.OnColor().G, Themes.OnColor().B),
            Color.FromArgb(0, Themes.OnColor().R, Themes.OnColor().G, Themes.OnColor().B));
        dc.DrawEllipse(glow, null, new Point(cx, cy), w * 0.42, h * 0.42);

        Pet.Paint(dc, cx, cy, s, 1.0, lx, ly, near);
    }
}