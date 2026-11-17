using System;
using System.Windows;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Controls;

/// <summary>
/// 原创小形象"发光双眼"（对应 Python ui/compact.py 的 PetFace）。
/// 表情：default / happy / wink / love / star / shy / pout / squint / surprised / tired。
/// </summary>
public sealed class PetFace
{
    private static readonly string[] Moods = { "happy", "wink", "love", "star", "shy", "pout", "squint" };

    private readonly Spring _ex = new(0, 140, 0.8, 0.01);
    private readonly Spring _ey = new(0, 140, 0.8, 0.01);
    private readonly Spring _lh = new(0, 160, 0.75, 0.003);
    private readonly Spring _rh = new(0, 160, 0.75, 0.003);
    private readonly Spring _tired = new(0, 60, 1.0, 0.003);
    private readonly Spring _wide = new(0, 220, 0.45, 0.003);
    private readonly Spring _morph = new(0, 200, 0.7, 0.003);
    private readonly Spring _blush = new(0, 120, 0.9, 0.003);

    private string _mood = "default";
    private string _lastMood = "";
    private bool _near;
    private DateTime _blinkAt = DateTime.UtcNow.AddSeconds(2.5);
    private DateTime _lastAttention = DateTime.UtcNow;
    private DateTime _surpriseUntil = DateTime.MinValue;
    private DateTime _t = DateTime.UtcNow;
    private string _m = "default";

    public double Spread { get; set; }
    public double Tilt { get; set; }

    private static readonly Random Rng = new();

    public void React(string kind = "surprise")
    {
        _lastAttention = DateTime.UtcNow;
        if (kind == "surprise")
        {
            _surpriseUntil = DateTime.UtcNow.AddSeconds(1.4);
            _wide.V += 9.0;
        }
    }

    private void Pick()
    {
        // 不与上次重复
        string choice;
        do { choice = Moods[Rng.Next(Moods.Length)]; } while (choice == _lastMood && Moods.Length > 1);
        _mood = choice;
        _lastMood = choice;
    }

    /// <summary>推进状态并返回眨眼程度（0..1）。</summary>
    private double Step(double lookX, double lookY, bool near, double s)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _t).TotalSeconds, 0.001, 0.05);
        _t = now;

        if (near && !_near) Pick();
        else if (!near && _near) _mood = "default";
        _near = near;
        if (near) _lastAttention = now;

        var m = near ? _mood : "default";
        if (now < _surpriseUntil) m = "surprise";
        _m = m;

        double tx, ty;
        if (!near && Math.Abs(lookX) > 420 * s)
        {
            var t = (now - DateTime.MinValue).TotalSeconds;
            tx = Math.Sin(t * 0.55) * 0.8 + Math.Sin(t * 1.7) * 0.15;
            ty = Math.Sin(t * 0.37) * 0.35;
        }
        else
        {
            tx = Anim.Clamp(lookX / (90.0 * s), -1, 1);
            ty = Anim.Clamp(lookY / (60.0 * s), -1, 1);
        }
        if (m == "shy") { tx = tx * 0.3 + 0.5; ty = 0.75; }

        _ex.To(tx);
        _ey.To(ty);
        _lh.To(m is "happy" or "laugh" or "wink" ? 1 : m == "shy" ? 0.55 : 0);
        _rh.To(m is "happy" or "laugh" or "wink" ? 1 : m == "shy" ? 0.55 : 0);
        _tired.To((now - _lastAttention).TotalSeconds > 60 && m == "default" ? 0.55 : 0.0);
        _wide.To(m switch { "star" => 0.3, "surprise" => 0.35, _ => 0.0 });
        _morph.To(m is "love" or "squint" or "pout" ? 1.0 : 0.0);
        _blush.To(m switch { "shy" => 1.0, "love" => 0.8, "happy" => 0.5, "laugh" => 0.6, "wink" => 0.5, _ => 0.0 });

        foreach (var sp in new[] { _ex, _ey, _lh, _rh, _tired, _wide, _morph, _blush }) sp.Step(dt);

        // 眨眼
        var dur = 0.16 + 0.18 * _tired.X;
        if (now >= _blinkAt.AddSeconds(dur))
        {
            var gap = 2.4 + (Math.Sin(now.TimeOfDay.TotalSeconds * 12.9898) * 0.5 + 0.5) * 3.2;
            _blinkAt = now.AddSeconds(gap * (_tired.X > 0.3 ? 1.6 : 1.0));
        }
        var b = 0.0;
        if (now >= _blinkAt && (now - _blinkAt).TotalSeconds < dur && m is "default" or "pout" or "shy" or "star")
            b = Math.Sin((now - _blinkAt).TotalSeconds / dur * Math.PI);
        return b;
    }

    private static Geometry Heart(double cx, double cy, double size)
    {
        var w = size;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(cx, cy + w * 0.42), true, true);
            ctx.BezierTo(new Point(cx - w * 0.9, cy - w * 0.05), new Point(cx - w * 0.45, cy - w * 0.75),
                new Point(cx, cy - w * 0.28), true, false);
            ctx.BezierTo(new Point(cx + w * 0.45, cy - w * 0.75), new Point(cx + w * 0.9, cy - w * 0.05),
                new Point(cx, cy + w * 0.42), true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Geometry Sparkle(double cx, double cy, double r)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(cx, cy - r), true, true);
            ctx.QuadraticBezierTo(new Point(cx, cy), new Point(cx + r, cy), true, false);
            ctx.QuadraticBezierTo(new Point(cx, cy), new Point(cx, cy + r), true, false);
            ctx.QuadraticBezierTo(new Point(cx, cy), new Point(cx - r, cy), true, false);
            ctx.QuadraticBezierTo(new Point(cx, cy), new Point(cx, cy - r), true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Geometry RoundedEye(Rect r, double radius)
    {
        var w = Math.Max(1, r.Width);
        var h = Math.Max(1, r.Height);
        return new RectangleGeometry(new Rect(r.X, r.Y, w, h), radius, radius);
    }

    /// <summary>绘制小形象。lookX/lookY 为光标相对中心的位置，near 表示光标靠近。</summary>
    public void Paint(DrawingContext dc, double cx, double cy, double s, double a, double lookX, double lookY, bool near)
    {
        var b = Step(lookX, lookY, near, s);
        var m = _m;
        var now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        var tired = Anim.Clamp(_tired.X, 0, 1);
        var wide = Anim.Clamp(_wide.X, -0.3, 1.0);
        var morph = Anim.Clamp(_morph.X, 0, 1);
        var lx = _ex.X;
        var ly = _ey.X;
        var bounce = m == "laugh" ? Math.Sin(now * 16) * 1.6 * s : 0.0;
        var baseW = 11.0 * s;
        var baseH = 15.0 * s;
        var gap = 15.0 * s;

        var eyeCol = Themes.Text(0.95 * a);
        var glowCol = Themes.IsDark ? Themes.OnColor() : Colors.Transparent;
        var pink = Color.FromRgb(255, 92, 138);
        var centers = new (double X, double Y)[2];

        for (var side = -1; side <= 1; side += 2)
        {
            var hap = Anim.Clamp(side < 0 ? _lh.X : _rh.X, 0, 1);
            var curious = 1.0 + 0.22 * Math.Max(0.0, lx * side);
            var w = baseW * (1 + 0.12 * wide) * (1 + 0.25 * b);
            var h = baseH * curious * (1 + 0.18 * wide) * (1 - 0.88 * b);
            var ex = cx + side * (gap / 2 + baseW / 2 + Spread * s / 2) + lx * 5.0 * s;
            var ey = cy + ly * 3.0 * s + bounce;
            centers[side < 0 ? 0 : 1] = (ex, ey);
            var eyeRect = new Rect(ex - w / 2, ey - h / 2, Math.Max(1, w), Math.Max(1.6 * s, h));

            // 光晕
            if (glowCol.A > 0 && a > 0.05)
            {
                var c1 = m == "love" ? pink : glowCol;
                c1 = Color.FromArgb((byte)Math.Round(85 * a), c1.R, c1.G, c1.B);
                var brush = new RadialGradientBrush(c1, Color.FromArgb(0, c1.R, c1.G, c1.B));
                dc.DrawEllipse(brush, null, new Point(eyeRect.X + eyeRect.Width / 2, eyeRect.Y + eyeRect.Height / 2),
                    eyeRect.Width * 0.95, eyeRect.Height * 0.75 + 3 * s);
            }

            // 爱心眼
            if (m == "love" && morph > 0.05)
            {
                var pulse = 1.0 + 0.08 * Math.Sin(now * 7);
                var hc = Color.FromArgb((byte)Math.Round(255 * a * morph), pink.R, pink.G, pink.B);
                dc.DrawGeometry(new SolidColorBrush(hc), null, Heart(ex, ey, 15 * s * pulse * (0.6 + 0.4 * morph)));
                continue;
            }
            // 眯眼 ><
            if (m == "squint" && morph > 0.05)
            {
                var pen = new Pen(new SolidColorBrush(eyeCol), 2.6 * s)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var d = -side;
                var k = 5.5 * s * morph;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(ex - d * k, ey - k), false, false);
                    ctx.LineTo(new Point(ex + d * k, ey), true, false);
                    ctx.LineTo(new Point(ex - d * k, ey + k), true, false);
                }
                dc.DrawGeometry(null, pen, geo);
                continue;
            }

            var rad = Math.Min(w, eyeRect.Height) * 0.42;
            Geometry eye = RoundedEye(eyeRect, rad);
            if (tired > 0.01)
            {
                var lid = new RectangleGeometry(new Rect(eyeRect.X - 2, eyeRect.Y - 2, eyeRect.Width + 4,
                    2 + eyeRect.Height * 0.5 * tired));
                eye = new CombinedGeometry(GeometryCombineMode.Exclude, eye, lid);
            }
            if (m == "pout" && morph > 0.05)
            {
                var lid = new RectangleGeometry(new Rect(-eyeRect.Width, -eyeRect.Height,
                    eyeRect.Width * 2.2, eyeRect.Height));
                var tg = new TransformGroup();
                tg.Children.Add(new RotateTransform(side * -24 * morph));
                tg.Children.Add(new TranslateTransform(eyeRect.X + eyeRect.Width / 2 - side * eyeRect.Width * 0.1,
                    eyeRect.Y + eyeRect.Height * 0.32 * morph));
                lid.Transform = tg;
                eye = new CombinedGeometry(GeometryCombineMode.Exclude, eye, lid);
            }
            if (hap > 0.01)
            {
                var cutR = eyeRect.Width * 0.9;
                var ccx = eyeRect.X + eyeRect.Width / 2;
                var ccy = eyeRect.Y + eyeRect.Height + cutR - eyeRect.Height * 0.62 * hap;
                var lid = new EllipseGeometry(new Point(ccx, ccy), cutR, cutR);
                eye = new CombinedGeometry(GeometryCombineMode.Exclude, eye, lid);
            }
            eye.Freeze();
            dc.DrawGeometry(new SolidColorBrush(eyeCol), null, eye);

            if (m == "star" && eyeRect.Height > 6 * s)
            {
                var sc = Themes.IsDark ? Color.FromArgb(200, 30, 30, 40) : Color.FromArgb(220, 20, 20, 26);
                var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(sc.A * a), sc.R, sc.G, sc.B));
                dc.DrawGeometry(brush, null, Sparkle(ex + 1.4 * s, ey - 2.2 * s, 3.4 * s));
                dc.DrawGeometry(brush, null, Sparkle(ex - 2.2 * s, ey + 3.0 * s, 1.6 * s));
            }
        }

        // 腮红
        var bl = Anim.Clamp(_blush.X, 0, 1);
        if (bl > 0.02)
        {
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1 : 1;
                var c = Color.FromArgb((byte)Math.Round(150 * bl * a), 255, 124, 162);
                dc.DrawEllipse(new SolidColorBrush(c), null,
                    new Point(centers[i].X + side * 7.5 * s, centers[i].Y + baseH * 0.55), 4.4 * s, 2.4 * s);
            }
        }

        // 嘴
        var mx = cx + lx * 5.0 * s;
        var my = cy + baseH * 0.66 + ly * 3.0 * s + bounce;
        var mc = Themes.Text(0.9 * a);
        var penM = new Pen(new SolidColorBrush(mc), 1.8 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        switch (m)
        {
            case "happy":
            case "wink":
            case "love":
            case "shy":
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var mw = 7.0 * s;
                    ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                    ctx.QuadraticBezierTo(new Point(mx, my + 4.2 * s), new Point(mx + mw / 2, my), true, false);
                }
                dc.DrawGeometry(null, penM, geo);
                break;
            }
            case "laugh":
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var mw = 9.0 * s;
                    ctx.BeginFigure(new Point(mx - mw / 2, my - 0.5 * s), true, true);
                    ctx.QuadraticBezierTo(new Point(mx, my + 8.5 * s), new Point(mx + mw / 2, my - 0.5 * s), true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(mc), null, geo);
                break;
            }
            case "star":
            case "surprise":
                dc.DrawEllipse(null, penM, new Point(mx, my + 1.5 * s), 2.4 * s, 2.8 * s);
                break;
            case "pout":
                dc.DrawLine(penM, new Point(mx - 3 * s, my + 1.5 * s), new Point(mx + 3 * s, my + 1.5 * s));
                break;
            case "squint":
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var mw = 6.0 * s;
                    ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                    ctx.QuadraticBezierTo(new Point(mx, my + 3.5 * s), new Point(mx + mw / 2, my), true, false);
                }
                dc.DrawGeometry(null, penM, geo);
                break;
            }
        }
    }
}