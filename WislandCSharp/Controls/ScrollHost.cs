using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisland.Controls;

/// <summary>
/// 轻量垂直滚动容器（替代 ScrollViewer）。
///
/// 为什么不用 ScrollViewer：在 AllowsTransparency 的窗口里，ScrollViewer 采用
/// "像素滚动"，滚动后旧位置的像素不会被清除，表现为毛玻璃层里出现滚动残影。
/// 这里改为每次滚动直接重新排列子元素（arrange 偏移），子树是真正移动而不是平移位图，
/// 因此不会残留；同时支持弹簧平滑动画。
/// </summary>
public sealed class ScrollHost : Decorator
{
    /// <summary>
    /// 滚动区底板。给一个不透明度较高的颜色可以彻底消除滚动残影：
    /// 半透明内容滚动时，旧位置的像素只能被父层重绘覆盖，
    /// 而父层本身半透明时旧内容仍会透出来（表现为"滚动残影"）。
    /// </summary>
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(ScrollHost),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        if (Background is { } bg)
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        // 右侧细滚动指示条
        if (_max > 1)
        {
            var contentH = h + _max;
            var trackTop = 4.0;
            var trackH = h - 8;
            var thumbH = Math.Max(28, trackH * (h / contentH));
            var t = _max <= 0 ? 0 : Math.Clamp(_y.X / _max, 0, 1);
            var thumbTop = trackTop + (trackH - thumbH) * t;

            var track = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            var thumb = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
            dc.DrawRoundedRectangle(track, null, new Rect(w - 7, trackTop, 4, trackH), 2, 2);
            dc.DrawRoundedRectangle(thumb, null, new Rect(w - 7, thumbTop, 4, thumbH), 2, 2);
        }
    }

    private readonly Spring _y = new(0, 520, 1.0, 0.6);
    private readonly TranslateTransform _shift = new();
    private double _max;
    private double _target;
    private bool _hooked;

    public ScrollHost()
    {
        ClipToBounds = true;
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
    }

    /// <summary>滚动位置变化（用于更新滚动条）。</summary>
    public event EventHandler? Scrolled;

    public double Offset => _y.X;

    public double MaxOffset => _max;

    public double ViewportHeight => ActualHeight;

    public double ContentHeight => Child?.DesiredSize.Height ?? 0;

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_y.Rest) return;
        _y.Step(1.0 / 60.0);
        _shift.Y = -_y.X;
        InvalidateVisual();   // 更新滚动指示条
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 逐级向上重绘祖先。
    /// 子元素移动后，它腾出来的那块区域需要由父层（玻璃底）重新绘制，
    /// 否则那部分像素保持上一帧内容 → 表现为"滚动残影"。
    /// </summary>
    private void RepaintAncestors() => UiRepaint.Ancestors(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        Child?.Measure(new Size(w, double.PositiveInfinity));
        var h = double.IsInfinity(availableSize.Height)
            ? Child?.DesiredSize.Height ?? 0
            : availableSize.Height;
        return new Size(w, Math.Min(h, Child?.DesiredSize.Height ?? h));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var contentH = Child?.DesiredSize.Height ?? 0;
        _max = Math.Max(0, contentH - finalSize.Height);

        if (_y.X > _max) _y.Snap(_max);
        if (_target > _max) _target = _max;

        // 子元素按内容完整高度排列在原点，滚动通过渲染变换平移：
        // 平移由合成器处理，不会留下旧位置像素（arrange 偏移会）。
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(contentH, finalSize.Height)));
        Child?.SetValue(RenderTransformProperty, _shift);
        _shift.Y = -_y.X;
        return finalSize;
    }

    /// <summary>内容高度可能变化后调用，重新计算可滚动范围。</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateArrange();
        RepaintAncestors();
    }

    public void ScrollToTop(bool immediate = true)
    {
        _target = 0;
        if (immediate) { _y.Snap(0); _shift.Y = 0; }
    }

    public void ScrollToEnd(bool immediate = true)
    {
        _target = _max;
        if (immediate) { _y.Snap(_max); _shift.Y = -_max; }
    }

    private void ScrollBy(double delta)
    {
        _target = Math.Clamp(_target + delta, 0, _max);
        _y.Target = _target;
        _y.Step(1.0 / 120.0);
        _shift.Y = -_y.X;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // 一次滚轮 60px，和系统行高体感接近
        ScrollBy(-e.Delta / 120.0 * 60.0);
        e.Handled = true;
        base.OnMouseWheel(e);
    }
}