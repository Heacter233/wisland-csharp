using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisland.Services;

namespace Wisland.Controls;

/// <summary>
/// 背景模糊：截取窗口身后的桌面画面 → 大幅缩小（等价高斯模糊）→ 作为画刷填充胶囊。
/// 对应 Python ui/glass.py 的 Backdrop：这样毛玻璃严格跟随胶囊形状与尺寸，不会出现矩形糊块。
/// 窗口自身用 WDA_EXCLUDEFROMCAPTURE 排除在截图之外，避免"照到自己"。
/// </summary>
public sealed class Backdrop : IDisposable
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint WdaNone = 0x00000000;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    // 缩小倍数：越大越糊、越省 CPU
    private const int Downscale = 7;
    private const int CaptureIntervalMs = 45;
    private const int IdleIntervalMs = 260;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines,
        byte[] bits, ref BITMAPINFO info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER header;
        public int colors;
    }

    private readonly object _lock = new();
    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _worker;
    private volatile bool _running;
    private bool _dumped;
    private int _reqX, _reqY, _reqW, _reqH;
    private double _reqDipW = 1, _reqDipH = 1;
    private volatile bool _dirty = true;
    private DateTime _lastCapture = DateTime.MinValue;
    private ImageBrush? _brush;

    /// <summary>当前可用的模糊背景画刷（已冻结，可跨线程读取）。</summary>
    public ImageBrush? Brush
    {
        get { lock (_lock) return _brush; }
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try { SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); }
        catch { }
    }

    public void Detach()
    {
        try
        {
            if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WdaNone);
        }
        catch { }
    }

    /// <summary>请求截取指定窗口区域（窗口坐标 = 屏幕坐标，因为窗口紧贴身体）。</summary>
    public void Request(double dipWidth, double dipHeight)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(_hwnd, out var r)) return;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        lock (_lock)
        {
            if (r.Left != _reqX || r.Top != _reqY || w != _reqW || h != _reqH ||
                Math.Abs(dipWidth - _reqDipW) > 0.5 || Math.Abs(dipHeight - _reqDipH) > 0.5)
            {
                _reqX = r.Left; _reqY = r.Top; _reqW = w; _reqH = h;
                _reqDipW = dipWidth; _reqDipH = dipHeight;
                _dirty = true;
            }
        }
        EnsureWorker();
    }

    /// <summary>标记内容需要刷新（位置变化/展开动画中）。</summary>
    public void Invalidate() => _dirty = true;

    private void EnsureWorker()
    {
        if (_worker != null) return;
        _running = true;
        _worker = new Thread(Loop) { IsBackground = true, Name = "wisland-backdrop" };
        _worker.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            var interval = _dirty ? CaptureIntervalMs : IdleIntervalMs;
            var wait = interval - (DateTime.UtcNow - _lastCapture).TotalMilliseconds;
            if (wait > 5) { Thread.Sleep((int)wait); continue; }

            int x, y, w, h;
            double dw, dh;
            lock (_lock)
            {
                x = _reqX; y = _reqY; w = _reqW; h = _reqH; dw = _reqDipW; dh = _reqDipH;
                _dirty = false;
            }
            _lastCapture = DateTime.UtcNow;
            if (w <= 0 || h <= 0) continue;

            var source = Capture(x, y, w, h);
            if (source == null) continue;
            var brush = MakeBrush(source, dw, dh);
            if (brush == null) continue;
            lock (_lock)
            {
                _brush = brush;
            }
        }
    }

    /// <summary>截屏指定区域，缩到 1/Downscale。</summary>
    private static BitmapSource? Capture(int x, int y, int w, int h)
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            memDc = CreateCompatibleDC(screenDc);
            bmp = CreateCompatibleBitmap(screenDc, w, h);
            if (memDc == IntPtr.Zero || bmp == IntPtr.Zero) return null;
            old = SelectObject(memDc, bmp);
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SrcCopy | CaptureBlt)) return null;

            var info = new BITMAPINFO
            {
                header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = w,
                    biHeight = -h,          // 负数 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                }
            };
            var stride = w * 4;
            var bytes = new byte[stride * h];
            if (GetDIBits(memDc, bmp, 0, (uint)h, bytes, ref info, 0) == 0) return null;

            var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
            full.Freeze();

            // 大幅缩小 = 廉价模糊
            var tw = Math.Max(4, w / Downscale);
            var th = Math.Max(4, h / Downscale);
            var scaled = new TransformedBitmap(full,
                new ScaleTransform((double)tw / w, (double)th / h));
            scaled.Freeze();
            return scaled;
        }
        catch { return null; }
        finally
        {
            if (memDc != IntPtr.Zero && old != IntPtr.Zero) SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>调试：把抓到的背景图落盘，用于确认是否抓到了自身画面。</summary>
    private static void DumpPng(BitmapSource source, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    private static ImageBrush? MakeBrush(BitmapSource source, double dipW, double dipH)
    {
        try
        {
            var brush = new ImageBrush(source)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, source.PixelWidth, source.PixelHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, Math.Max(1, dipW), Math.Max(1, dipH)),
                TileMode = TileMode.None,
            };
            brush.Freeze();
            return brush;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _running = false;
        Detach();
    }
}