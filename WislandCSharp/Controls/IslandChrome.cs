using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Controls;

/// <summary>
/// 灵动岛本体（对应 Python ui/island.py 的 Island 的绘制与形变部分）：
/// - 固定尺寸窗口内的透明面板，绘制毛玻璃胶囊（圆角／贴边内凹圆角／描边／高光／阴影）；
/// - 收起 / 展开尺寸由阻尼弹簧驱动（欠阻尼回弹）；
/// - 鼠标按压产生"挤压"形变，光标附近凹陷（简化版 poke）。
/// </summary>
public sealed class IslandChrome : Panel
{
    public const double Pad = 12;
    public const double NavH = 36;
    public const double Fillet = 13.0;

    /// <summary>窗口四周留给阴影/形变的余量（窗口 = 本体 + 2×WindowPad）。</summary>
    public const double WindowPad = 22;

    private readonly Spring _w = new(340, 480, 0.95, 0.2);
    private readonly Spring _h = new(54, 480, 0.95, 0.2);
    private readonly Spring _compactA = new(1, 700, 1.0, 0.01);
    private readonly Spring _contentA = new(0, 700, 1.0, 0.01);
    private readonly Spring _poke = new(0, 380, 0.55, 0.004);

    private bool _expanded;
    private double _pokeX, _pokeY;
    private bool _poking;
    private Rect _bodyRect;
    private Point _pressPoint = new(-1, -1);
    private DateTime _t0 = DateTime.UtcNow;

    public CompactView Compact { get; }
    public FrameworkElement ExpandedHost { get; set; }

    /// <summary>展开后目标高度（由页面内容决定）。</summary>
    public Func<double>? ExpandedHeight { get; set; }

    public bool IsExpanded => _expanded;

    /// <summary>背景模糊（只填充胶囊路径，跟随尺寸）。</summary>
    public Backdrop? Backdrop { get; set; }

    /// <summary>当前本体尺寸（动画中的实时值）。</summary>
    public (double W, double H) BodySize => (Math.Max(40, _w.X), Math.Max(24, _h.X));

    public event EventHandler? ExpandRequested;
    public event EventHandler? CollapseRequested;
    public event EventHandler? DragRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler<int>? ViewCycled;

    public IslandChrome(CompactView compact, FrameworkElement expandedHost)
    {
        Compact = compact;
        ExpandedHost = expandedHost;
        Children.Add(Compact);
        Children.Add(ExpandedHost);
        ExpandedHost.Visibility = Visibility.Collapsed;
    }

    // ---------------- 尺寸/动画 ----------------
    public void SnapSize()
    {
        var (cw, ch) = Compact.SizeHint();
        _w.Snap(cw);
        _h.Snap(ch);
        _compactA.Snap(1);
        _contentA.Snap(0);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        var h = ExpandedHeight?.Invoke() ?? 320;
        _compactA.To(0, 900, 1.0);
        _contentA.Snap(0);
        _contentA.To(1, 700, 1.0);
        _w.To(640, 440, 0.6);
        _h.To(h, 440, 0.62);
        ExpandedHost.Visibility = Visibility.Visible;
        ExpandedHost.Opacity = 0;
        Compact.Visibility = Visibility.Visible;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _compactA.To(1, 500, 1.0);
        _contentA.To(0, 400, 1.0);
        var (cw, ch) = Compact.SizeHint();
        _w.To(cw, 460, 0.64);
        _h.To(ch, 460, 0.64);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void RetargetCompact()
    {
        if (_expanded) return;
        var (cw, ch) = Compact.SizeHint();
        if (Math.Abs(_w.Target - cw) > 0.5 || Math.Abs(_h.Target - ch) > 0.5)
        {
            _w.To(cw, 460, 0.64);
            _h.To(ch, 460, 0.64);
            InvalidateMeasure();
        }
    }

    /// <summary>展开状态下改变目标高度（切页时用）。</summary>
    public void RetargetExpandedHeight(double height)
    {
        if (!_expanded) return;
        if (Math.Abs(_h.Target - height) > 0.5)
        {
            _h.To(height, 440, 0.62);
            InvalidateMeasure();
        }
    }

    /// <summary>当前本体的圆角半径（与 BodyGeometry 中一致）。</summary>
    public double BodyCornerRadius
    {
        get
        {
            var bw = Math.Max(40, _w.X);
            var bh = Math.Max(24, _h.X);
            var squash = 0.0;
            if (_expanded || bh > 60) squash = Math.Min(30.0, bh / 2);
            return _expanded ? squash : Math.Min(bh / 2, Math.Min(bw / 2, 30.0));
        }
    }

    /// <summary>
    /// 模糊区域相对本体的外扩量。
    /// 取 0：accent 模糊是窗口矩形级的，而 SetWindowRgn 对分层窗口不生效（实测），
    /// 外扩多少就会露出多少矩形亮边，所以窗口紧贴胶囊包围盒。
    /// </summary>
    public const double GlassRegionMargin = 0.0;

    /// <summary>当前是否处于尺寸/内容动画中（供宿主降低开销用）。</summary>
    public bool Animating { get; private set; }

    /// <summary>每帧推进；返回是否仍需继续动画。</summary>
    public bool Step(double dt)
    {
        foreach (var s in new[] { _w, _h, _compactA, _contentA, _poke }) s.Step(dt);
        var moving = !(_w.Rest && _h.Rest && _compactA.Rest && _contentA.Rest && _poke.Rest);
        Animating = moving;

        if (_expanded && !_contentA.Rest)
            ExpandedHost.Opacity = ContentOpacity(_contentA.X);
        Compact.Opacity = Anim.Clamp(_compactA.X, 0, 1);

        // 收起态内容：展开时淡出并隐藏，避免与页面内容叠画
        if (_expanded && _compactA.X < 0.02 && Compact.Visibility == Visibility.Visible)
            Compact.Visibility = Visibility.Collapsed;
        else if (Compact.Visibility == Visibility.Collapsed && (!_expanded || _compactA.Target > 0.5))
            Compact.Visibility = Visibility.Visible;
        if (!_expanded && _contentA.Rest && _contentA.X < 0.02 && ExpandedHost.Visibility == Visibility.Visible)
            ExpandedHost.Visibility = Visibility.Collapsed;
        if (!_expanded) ExpandedHost.Opacity = ContentOpacity(_contentA.X);

        // 收起后尺寸跟随内容
        if (!_expanded && _contentA.X < 0.12 && _w.Rest && _h.Rest) RetargetCompact();

        // 重绘节流：动画中逐帧重绘；静止时只在收起态以约 20fps 重绘（边缘流光）
        // 恒定 60fps 全窗重绘会在深色/大窗下表现为持续占用与卡顿感。
        _idleTick++;
        var needRepaint = moving || (!_expanded && _idleTick % 3 == 0);
        if (needRepaint) InvalidateVisual();
        return moving;
    }

    private int _idleTick;

    /// <summary>
    /// 内容不透明度曲线：用 sqrt 提前拉起、延后落下。
    /// 目的是让"内容出现/消失"与尺寸动画错开——否则展开中途会出现一帧
    /// 胶囊已经变大但内容还是透明的（深色模式下透出亮背景 → 闪烁）。
    /// </summary>
    private static double ContentOpacity(double progress)
    {
        var p = Anim.Clamp(progress, 0, 1);
        return Math.Pow(p, 0.42);
    }

    // ---------------- 布局 ----------------
    protected override Size MeasureOverride(Size availableSize)
    {
        var bw = Math.Max(40, _w.X);
        var bh = Math.Max(24, _h.X);

        // 两个子元素都测量（不能只测当前可见的，否则首次展开时页面尺寸为 0 → 空白）
        var inner = new Size(Math.Max(0, bw - Pad * 2), Math.Max(0, bh - Pad * 2));

        // 展开内容按「目标尺寸」测量，而不是随动画变化的当前尺寸：
        // 系统模糊模式下窗口会逐帧缩放，若按当前尺寸测量，
        // 页面（卡片列表）每帧都要完整重新测量 → 收起动画中段明显卡顿。
        if (_expanded)
        {
            var targetH = Math.Max(24, _h.Target);
            var targetW = Math.Max(40, _w.Target);
            inner = new Size(
                Math.Max(inner.Width, targetW - Pad * 2),
                Math.Max(inner.Height, targetH - Pad * 2));
        }

        ExpandedHost.Measure(inner);
        Compact.Measure(new Size(bw, bh));
        return availableSize;
    }

    /// <summary>
    /// 让本体铺满窗口（窗口尺寸 = 本体 + FillMargin）。
    /// 系统模糊模式下必须这样：accent 模糊是窗口矩形级的，只有窗口贴合胶囊，
    /// 模糊才会跟着胶囊走；否则收起态会出现一大片矩形模糊。
    /// </summary>
    public bool FillWindow { get; set; }

    /// <summary>FillWindow 模式下的四周边距（阴影落在其中）。</summary>
    public double FillMargin { get; set; } = GlassRegionMargin;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bw = Math.Max(40, _w.X);
        var bh = Math.Max(24, _h.X);

        if (FillWindow)
        {
            _bodyRect = new Rect(FillMargin, FillMargin, bw, bh);
        }
        else
        {
            // 窗口尺寸固定，本体按贴边方向放在窗口对应一侧
            double x, y;
            switch (AnchorEdge)
            {
                case "bottom":
                    x = (finalSize.Width - bw) / 2;
                    y = finalSize.Height - WindowPad - bh;
                    break;
                case "left":
                    x = WindowPad;
                    y = (finalSize.Height - bh) / 2;
                    break;
                case "right":
                    x = finalSize.Width - WindowPad - bw;
                    y = (finalSize.Height - bh) / 2;
                    break;
                default: // top
                    x = (finalSize.Width - bw) / 2;
                    y = WindowPad;
                    break;
            }
            _bodyRect = new Rect(x, y, bw, bh);
        }

        if (_expanded)
        {
            ExpandedHost.Arrange(_bodyRect);
            Compact.Arrange(_bodyRect);
        }
        else
        {
            Compact.Arrange(_bodyRect);
            ExpandedHost.Arrange(_bodyRect);
        }
        return finalSize;
    }

    /// <summary>贴边方向：top / bottom / left / right（决定本体贴在窗口的哪一侧）。</summary>
    public string AnchorEdge { get; set; } = "top";

    // ---------------- 形状 ----------------
    private Geometry BodyGeometry()
    {
        var r = _bodyRect;
        var bw = r.Width;
        var bh = r.Height;

        var squash = 0.0;
        if (_expanded || bh > 60) squash = Math.Min(30.0, bh / 2);
        var baseR = _expanded ? squash : Math.Min(bh / 2, Math.Min(bw / 2, 30.0));

        // 按压形变：沿远离屏幕边方向压扁（简化：整体缩放）
        var pk = Anim.Clamp(_poke.X, -0.5, 1.2);
        var w = bw;
        var h = bh;
        if (Math.Abs(pk) > 0.002 && !_expanded)
        {
            h = bh * (1 - 0.10 * pk);
            w = bw * (1 + 0.03 * pk);
        }

        var rect = new Rect(r.X + (bw - w) / 2, r.Y + (bh - h) / 2, w, h);
        var radii = _expanded
            ? new[] { baseR, baseR, baseR, baseR }
            : new[] { h / 2, h / 2, h / 2, h / 2 };

        return RoundedRect(rect, radii);
    }

    /// <summary>四角可不同的圆角矩形（顺时针：左上、右上、右下、左下）。</summary>
    private static Geometry RoundedRect(Rect r, double[] radii)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var tl = Math.Min(radii[0], Math.Min(r.Width, r.Height) / 2);
            var tr = Math.Min(radii[1], Math.Min(r.Width, r.Height) / 2);
            var br = Math.Min(radii[2], Math.Min(r.Width, r.Height) / 2);
            var bl = Math.Min(radii[3], Math.Min(r.Width, r.Height) / 2);

            ctx.BeginFigure(new Point(r.X + tl, r.Y), true, true);
            ctx.LineTo(new Point(r.Right - tr, r.Y), true, false);
            ctx.ArcTo(new Point(r.Right, r.Y + tr), new Size(tr, tr), 0, false,
                SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - br), true, false);
            ctx.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false,
                SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.X + bl, r.Bottom), true, false);
            ctx.ArcTo(new Point(r.X, r.Bottom - bl), new Size(bl, bl), 0, false,
                SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.X, r.Y + tl), true, false);
            ctx.ArcTo(new Point(r.X + tl, r.Y), new Size(tl, tl), 0, false,
                SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    // ---------------- 绘制 ----------------
    protected override void OnRender(DrawingContext dc)
    {
        if (_bodyRect.Width < 10) return;
        var path = BodyGeometry();

        // 0) 药丸外的雾状柔边：主题色由内向外快速淡出，柔化玻璃与背景的接缝
        DrawHalo(dc, path);

        // 1) 阴影（几层半透明描边近似）
        for (var i = 0; i < 3; i++)
        {
            var wid = new[] { 14.0, 8.0, 4.0 }[i];
            var alpha = new byte[] { 5, 9, 14 }[i];
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), wid), path);
        }

        // 2) 毛玻璃底：真实背景模糊（跟随形状）→ 主题色调 → 顶部光泽
        dc.PushClip(path);
        var r = _bodyRect;

        var backdrop = SystemGlass ? null : Backdrop?.Brush;
        if (SystemGlass)
        {
            // 模糊由 DWM 在窗口背后实时完成，这里只叠色调与光泽
            dc.DrawRectangle(SystemGlassTint(), null, r);
        }
        else if (backdrop != null)
        {
            // 模糊层
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
            // 色调叠加（半透明，保证文字对比度）
            dc.DrawRectangle(TintedOverlay(), null, r);
        }
        else if (Themes.Current.Tint2 is not null)
        {
            var g = new LinearGradientBrush(Themes.Tint(), Themes.Current.Tint2.Value,
                new Point(0, 0), new Point(0.25, 1));
            dc.DrawRectangle(g, null, r);
        }
        else
        {
            dc.DrawRectangle(new SolidColorBrush(Themes.Tint()), null, r);
        }
        var gloss = new LinearGradientBrush(
            Color.FromArgb(Themes.IsDark ? (byte)14 : (byte)26, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255),
            new Point(0, 0), new Point(0, 1));
        gloss.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.52));
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(10, 255, 255, 255), 1.0));
        dc.DrawRectangle(gloss, null, r);
        dc.Pop();

        // 3) 轮廓：外暗线 + 内侧高光边
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Themes.Outline()), 1.6), path);
        var rim = new LinearGradientBrush(Themes.RimTop(), Themes.RimBottom(), new Point(0, 0), new Point(0, 1));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Themes.RimTop()), 1.0), path);

        // 4) 常态流光：沿边缘缓慢游走（收起态）
        if (!_expanded)
        {
            var t = (DateTime.UtcNow - _t0).TotalSeconds;
            var ph = Math.Sin(t * 0.55) * 0.5 + 0.5;
            var rad = new RadialGradientBrush(
                Color.FromArgb(Themes.IsDark ? (byte)55 : (byte)80, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(rays(rad)), 1.0), path);
        }

        // 5) 四角雾化：把矩形模糊在药丸圆角外露出的四块盖住
        DrawCornerFog(dc, path);
    }

    /// <summary>
    /// 药丸圆角外侧的四块区域（窗口矩形 − 药丸）用雾遮住。
    ///
    /// 径向渐变以窗口角为圆心：角上淡、靠近药丸浓，
    /// 于是玻璃边缘像"雾化散开"，而不是被一刀切成方角。
    /// </summary>
    private void DrawCornerFog(DrawingContext dc, Geometry pillPath)
    {
        if (!CornerFog) return;

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 8 || h < 8) return;

        var r = Math.Max(2, BodyCornerRadius);
        var rr = r * 2.0;
        var tint = Themes.Tint();
        // 只做"极轻"遮盖：目的不是把四角涂成玻璃，而是让模糊矩形的硬边界
        // 不至于在白底/照片背景上露成一块方形。太重会变成四块黑角（实测过）。
        var peak = CornerFogAlpha;
        if (!Themes.IsDark) peak = (byte)(peak * 0.85);
        if (peak == 0) return;
        var edge = (byte)(peak * 0.35);

        var outside = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, w, h)),
            pillPath);

        dc.PushClip(outside);
        try
        {
            var corners = new[]
            {
                new Point(0, 0),
                new Point(w, 0),
                new Point(0, h),
                new Point(w, h),
            };

            foreach (var c in corners)
            {
                var brush = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    Center = c,
                    GradientOrigin = c,
                    RadiusX = rr,
                    RadiusY = rr,
                };
                // 0 = 窗口角（模糊矩形的硬边界处），1 = 靠近药丸
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(edge, tint.R, tint.G, tint.B), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.75), tint.R, tint.G, tint.B), 0.45));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, tint.R, tint.G, tint.B), 0.85));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, tint.R, tint.G, tint.B), 1.0));

                var left = c.X <= 0.5 ? 0 : w - rr;
                var top = c.Y <= 0.5 ? 0 : h - rr;
                dc.DrawRectangle(brush, null, new Rect(left, top, rr, rr));
            }
        }
        finally { dc.Pop(); }
    }


    /// <summary>系统级背景模糊（DWM accent）：真实实时模糊，不需要抓屏。</summary>
    public bool SystemGlass { get; set; }

    /// <summary>
    /// 四角雾化遮盖：系统模糊（accent）的作用域是窗口矩形，
    /// 药丸圆角外侧那四块"本不该有玻璃"的区域会带上矩形模糊。
    /// 打开后用一层雾把这几块盖住，模糊边界就不会露成方角。
    /// </summary>
    public bool CornerFog { get; set; }

    /// <summary>
    /// 四角雾化强度（0~255）。默认 0 = 关闭。
    ///
    /// 实测结论：亚克力（ACCENT_ENABLE_ACRYLICBLURBEHIND）的模糊**只出现在窗口有 alpha 的像素处**，
    /// 药丸圆角外侧 alpha=0 的区域本来就是透明的、不会带模糊 —— 所以四角不需要遮盖。
    /// 之前的"四角方角/黑块"来自 CLASSIC blur-behind（state 3），它在分层窗口上是把窗口矩形涂黑。
    /// 这个旋钮只在对某些驱动/主题下想额外柔化边缘时才用。
    /// </summary>
    public byte CornerFogAlpha { get; set; }

    /// <summary>
    /// 药丸外的雾状柔边宽度（DIP）。0 = 关闭。
    /// 一圈由内向外迅速变淡的主题色描边，用来把玻璃边界"雾化"，
    /// 让药丸像是从背景里凝出来，而不是硬贴上去的实心块。
    /// </summary>
    public double HaloWidth { get; set; }

    /// <summary>
    /// 系统模糊模式下的色调。
    /// alpha 不能太高，否则会把 DWM 的实时模糊完全盖住（看起来只是一块半透明色）。
    /// </summary>
    /// <summary>
    /// 药丸外的雾状柔边。
    ///
    /// 做法：以药丸形状为中心画一圈同心描边，宽度递增、透明度按幂律衰减，
    /// 于是药丸外侧出现一段"雾"，越远越淡直到完全消失。
    /// 相比抓屏/系统模糊，这一层完全由我们绘制：形状永远跟着药丸走，
    /// 不会出现方角，也不会因为残影而闪。
    /// </summary>
    private void DrawHalo(DrawingContext dc, Geometry path)
    {
        var w = HaloWidth;
        if (w < 0.5) return;

        var tint = Themes.Tint();
        var peak = Themes.IsDark ? 132 : 96;
        const int steps = 9;

        for (var i = 0; i < steps; i++)
        {
            var t = (i + 1) / (double)steps;          // 0 → 贴近药丸，1 → 最外
            var stroke = 2.0 + t * w * 2.0;           // 居中描边 → 外扩 = stroke / 2
            var alpha = (byte)Math.Round(Math.Pow(1 - t, 2.2) * peak);
            if (alpha == 0) continue;
            var color = Color.FromArgb(alpha, tint.R, tint.G, tint.B);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), stroke), path);
        }

        // 内侧再叠一层极淡的白雾，让边缘有"毛玻璃凝霜"的质感
        var fogColor = Color.FromArgb(Themes.IsDark ? (byte)16 : (byte)34, 255, 255, 255);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(fogColor), w * 0.9), path);
    }

    private static Brush SystemGlassTint()
    {
        var tint = Themes.Tint();
        var a = (byte)(Themes.IsDark ? 150 : 140);
        var c1 = Color.FromArgb(a, tint.R, tint.G, tint.B);
        Color c2;
        if (Themes.Current.Tint2 is { } t2)
            c2 = Color.FromArgb(a, t2.R, t2.G, t2.B);
        else
            c2 = Color.FromArgb(a,
                (byte)Math.Min(255, tint.R + 8),
                (byte)Math.Min(255, tint.G + 8),
                (byte)Math.Min(255, tint.B + 10));
        return new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(0.25, 1));
    }

    /// <summary>模糊层之上的主题色调（半透明，让玻璃有主题色但不遮死背景）。</summary>
    private static Brush TintedOverlay()
    {
        var tint = Themes.Tint();
        // 有真实背景时降低色调不透明度，保留"透过去"的感觉
        var alpha = (byte)Math.Clamp(tint.A * 0.92, 60, 245);
        var c = Color.FromArgb(alpha, tint.R, tint.G, tint.B);
        if (Themes.Current.Tint2 is { } t2)
        {
            var c2 = Color.FromArgb((byte)Math.Clamp(t2.A * 0.92, 60, 245), t2.R, t2.G, t2.B);
            return new LinearGradientBrush(c, c2, new Point(0, 0), new Point(0.25, 1));
        }
        return new SolidColorBrush(c);
    }

    private static Color rays(RadialGradientBrush b) => Color.FromArgb(90, 255, 255, 255);

    // ---------------- 鼠标 ----------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!_bodyRect.Contains(p)) return;

        if (!_expanded)
        {
            // 播放 / 暂停按钮
            if (Compact.PlayRect is { } pr)
            {
                var local = new Rect(pr.X + _bodyRect.X, pr.Y + _bodyRect.Y, pr.Width, pr.Height);
                if (local.Contains(p))
                {
                    PlayPauseRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                }
            }
            _pressPoint = p;
            _poking = true;
            _pokeX = p.X;
            _pokeY = p.Y;
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_poke.Target != 0.0 || _poking)
        {
            _pokeX = p.X;
            _pokeY = p.Y;
        }
        if (_poking && _pressPoint.X >= 0 && (p - _pressPoint).Length > 4)
        {
            _poking = false;
            _poke.To(0, 300, 0.22);
            DragRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _poking = false;
        _poke.To(0, 300, 0.22);
        _pressPoint = new Point(-1, -1);
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!_expanded)
        {
            ViewCycled?.Invoke(this, e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }
        base.OnMouseWheel(e);
    }

    /// <summary>光标位于本体上时给按压弹簧一个目标值（由宿主每帧调用）。</summary>
    public void UpdatePoke(Point posInChrome, bool inside)
    {
        if (_expanded) { _poke.To(0, 300, 0.22); return; }
        if (inside && _bodyRect.Width > 1)
        {
            var nx = (posInChrome.X - _bodyRect.X - _bodyRect.Width / 2) / Math.Max(1, _bodyRect.Width / 2);
            var ny = (posInChrome.Y - _bodyRect.Y - _bodyRect.Height / 2) / Math.Max(1, _bodyRect.Height / 2);
            var depth = 1.0 - Math.Min(1.0, Math.Sqrt(nx * nx + ny * ny));
            var target = Math.Pow(Math.Max(0, depth), 1.4);
            if (Math.Abs(_poke.Target - target) > 0.01) _poke.To(target, 260, 0.75);
        }
        else if (_poke.Target != 0.0)
        {
            _poke.To(0, 300, 0.22);
        }
    }

    public Rect BodyRect => _bodyRect;
}
