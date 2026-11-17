using System;

namespace Wisland.Controls;

/// <summary>
/// 阻尼弹簧（对应 Python ui/anim.py 的 Spring）。
/// ratio &lt; 1 时带弹性回弹；积分用固定子步长保证稳定。
/// </summary>
public sealed class Spring
{
    public double X { get; set; }
    public double V { get; set; }
    public double Target { get; set; }
    private double _k;
    private double _c;
    private readonly double _eps;

    public Spring(double value = 0.0, double stiffness = 300.0, double ratio = 0.8, double eps = 0.05)
    {
        X = value;
        Target = value;
        _k = stiffness;
        _c = 2.0 * ratio * Math.Sqrt(stiffness);
        _eps = eps;
    }

    public void Tune(double stiffness, double ratio)
    {
        _k = stiffness;
        _c = 2.0 * ratio * Math.Sqrt(stiffness);
    }

    public void To(double target, double? stiffness = null, double? ratio = null)
    {
        Target = target;
        if (stiffness is { } k)
            Tune(k, ratio ?? _c / (2.0 * Math.Sqrt(_k)));
    }

    public void Snap(double value)
    {
        X = Target = value;
        V = 0.0;
    }

    public bool Rest => Math.Abs(X - Target) < _eps && Math.Abs(V) < _eps * 8;

    public void Step(double dt)
    {
        if (Rest)
        {
            X = Target;
            V = 0.0;
            return;
        }
        var n = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240.0)));
        var h = dt / n;
        for (var i = 0; i < n; i++)
        {
            var a = -_k * (X - Target) - _c * V;
            V += a * h;
            X += V * h;
        }
    }
}

public static class Anim
{
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Clamp(double x, double lo, double hi) => x < lo ? lo : (x > hi ? hi : x);

    public static double Smooth(double t)
    {
        t = Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static double EaseOut(double t)
    {
        t = Clamp(t, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }
}