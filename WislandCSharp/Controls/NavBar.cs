using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Controls;

/// <summary>
/// 顶部导航（对应 Python ui/nav.py 的 NavBar）：
/// 透明顶栏 + 图标组；当前项展开显示文字并带半透明圆角底；宽度由弹簧驱动。
/// </summary>
public sealed class NavBar : FrameworkElement
{
    public const double IconW = 34.0;
    public const double Gap = 16.0;

    private readonly List<(string Key, string Glyph, string Label)> _items;
    private readonly List<Spring> _widths = new();
    private readonly List<Spring> _hover = new();
    private readonly List<double> _fullWidths = new();
    private int _index;
    private int _hoverIndex = -1;
    private int _dragStart;

    public Dictionary<string, int> BadgeValues { get; } = new();
    public double Height2 { get; set; } = 36;

    public event EventHandler<string>? SelectionChanged;
    public event EventHandler? DragRequested;

    public IReadOnlyList<string> Keys => _items.Select(i => i.Key).ToList();

    public string Current => _items[_index].Key;

    public NavBar(IEnumerable<(string Key, string Glyph, string Label)> items)
    {
        _items = items.ToList();
        foreach (var (_, _, label) in _items)
        {
            _fullWidths.Add(IconW + Text.Width(label, 12.5, FontWeights.DemiBold) + 46);
            _widths.Add(new Spring(IconW, 260, 0.56, 0.05));
            _hover.Add(new Spring(0, 400, 1.0, 0.01));
        }
        if (_widths.Count > 0)
        {
            _widths[0].Snap(_fullWidths[0]);
        }
        IsHitTestVisible = true;
    }

    public void SetCurrent(string key, bool emit = false)
    {
        var i = _items.FindIndex(x => x.Key == key);
        if (i < 0 || i == _index) return;
        _index = i;
        for (var j = 0; j < _widths.Count; j++)
            _widths[j].To(j == i ? _fullWidths[j] : IconW);
        if (emit) SelectionChanged?.Invoke(this, key);
        InvalidateVisual();
    }

    /// <summary>取消选中（用于切换到插件页时，让高亮不落在主导航上）。</summary>
    public void ClearSelection()
    {
        for (var j = 0; j < _widths.Count; j++) _widths[j].To(IconW);
        _index = -1;
        InvalidateVisual();
    }

    public void SetBadge(string key, int n)
    {
        BadgeValues[key] = n;
        InvalidateVisual();
    }

    /// <summary>推进动画；返回是否仍在动画中。</summary>
    public bool Step(double dt)
    {
        var moving = false;
        foreach (var s in _widths) { s.Step(dt); moving |= !s.Rest; }
        foreach (var s in _hover) { s.Step(dt); moving |= !s.Rest; }
        InvalidateVisual();
        return moving;
    }

    private List<Rect> Rects()
    {
        var widths = _widths.Select(s => Math.Max(IconW * 0.7, s.X)).ToList();
        var n = widths.Count;
        var right = 34.0; // 预留右侧按钮
        var h = Math.Max(Height2, ActualHeight);
        var total = widths.Sum() + Gap * (n - 1);
        var left = Math.Max(2.0, (ActualWidth - right - total) / 2);
        var x = left;
        var list = new List<Rect>();
        foreach (var w in widths)
        {
            list.Add(new Rect(x, 2, w, h - 4));
            x += w + Gap;
        }
        return list;
    }

    private int Hit(Point p)
    {
        var rs = Rects();
        for (var i = 0; i < rs.Count; i++) if (rs[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var rects = Rects();
        if (rects.Count == 0) return;

        // 当前项底块（_index = -1 表示当前是插件页，主导航全部收起）
        if (_index >= 0 && _index < rects.Count)
        {
            var ar = rects[_index];
            dc.DrawRoundedRectangle(new SolidColorBrush(Themes.ActivePill()), null, ar, ar.Height / 2, ar.Height / 2);
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var r = rects[i];
            var active = i == _index;
            if (!active && _hover[i].X > 0.01)
            {
                var c = Themes.Hover();
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb((byte)(c.A * _hover[i].X * 0.8), c.R, c.G, c.B)),
                    null, r, r.Height / 2, r.Height / 2);
            }
            var a = active ? 1.0 : 0.55 + 0.4 * _hover[i].X;
            Text.DrawCenter(dc, _items[i].Glyph, 15, FontWeights.Normal,
                active ? Themes.PillText() : Themes.Text(a),
                new Point(r.X + IconW / 2, r.Y + r.Height / 2), icon: true);

            // 文字随宽度展开渐显
            var extra = _fullWidths[i] - IconW;
            var prog = Anim.Clamp((r.Width - IconW) / Math.Max(1, extra), 0, 1);
            if (prog > 0.02 && extra > 0)
            {
                dc.PushClip(new RectangleGeometry(new Rect(r.X, r.Y, r.Width, r.Height)));
                Text.Draw(dc, _items[i].Label, 12.5, FontWeights.DemiBold, Themes.PillText(prog),
                    new Rect(r.X + IconW - 4, r.Y, extra + 8, r.Height), TextAlign.Left);
                dc.Pop();
            }

            if (BadgeValues.TryGetValue(_items[i].Key, out var n) && n > 0)
            {
                dc.DrawEllipse(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff453a")), null,
                    new Point(r.X + IconW / 2 + 7, r.Y + 8), 3.2, 3.2);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = Hit(e.GetPosition(this));
        if (i != _hoverIndex)
        {
            if (_hoverIndex >= 0) _hover[_hoverIndex].To(0);
            if (i >= 0) _hover[i].To(1);
            _hoverIndex = i;
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
        if (_dragStart >= 0 && (e.GetPosition(this) - _lastPress).Length > 4)
        {
            _dragStart = -1;
            DragRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseMove(e);
    }

    private Point _lastPress = new(-1, -1);

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverIndex >= 0) { _hover[_hoverIndex].To(0); _hoverIndex = -1; InvalidateVisual(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var i = Hit(e.GetPosition(this));
        if (i >= 0)
        {
            SetCurrent(_items[i].Key, emit: true);
            _dragStart = -1;
        }
        else
        {
            _dragStart = i;
            _lastPress = e.GetPosition(this);
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var d = e.Delta > 0 ? -1 : 1;
        var ni = (int)Anim.Clamp(_index + d, 0, _items.Count - 1);
        SetCurrent(_items[ni].Key, emit: true);
        base.OnMouseWheel(e);
    }
}