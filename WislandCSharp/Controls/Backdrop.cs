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

    /// <summary>几何变化后等待稳定的时间，避免动画中反复抓屏（每次都需短暂隐身）。</summary>
    private const int SettleMs = 140;

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    // DWM：把窗口“隐身”出合成画面（比 ShowWindow 温和，不改窗口状态）
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private const int DwmwaCloak = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

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
    private DateTime _lastRequestAt = DateTime.MinValue;
    private ImageBrush? _brush;

    /// <summary>抓屏时是否临时隐身（cloak 自己），避免把自身画面拍进玻璃。</summary>
    public bool CloakWhenCapturing { get; set; } = true;

    /// <summary>已完成的抓屏次数（调试用）。</summary>
    public int CaptureCount { get; private set; }

    /// <summary>窗口自身是否成功从截屏中排除（失败时玻璃会抓到自己，产生鬼影）。</summary>
    public bool SelfExcluded { get; private set; }

    /// <summary>最后一次 SetWindowDisplayAffinity 的 Win32 错误码。</summary>
    public int LastExcludeError { get; private set; }

    /// <summary>
    /// 重新尝试把自身从截屏中排除。
    /// 分层窗口（AllowsTransparency）在句柄刚创建时调用可能失败，
    /// 需要等窗口真正显示后再试。
    /// </summary>
    public bool RetryExclude()
    {
        if (_hwnd == IntPtr.Zero) return false;
        try
        {
            LastExcludeError = 0;
            SelfExcluded = SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
            if (!SelfExcluded) LastExcludeError = Marshal.GetLastWin32Error();
            return SelfExcluded;
        }
        catch (Exception ex)
        {
            LastExcludeError = ex.HResult;
            SelfExcluded = false;
            return false;
        }
    }

    /// <summary>最近一次实际抓取的屏幕矩形（已裁剪到显示器内）。</summary>
    public int LastCaptureWidth => _capW;
    public int LastCaptureHeight => _capH;

    /// <summary>当前可用的模糊背景画刷（已冻结，可跨线程读取）。</summary>
    public ImageBrush? Brush
    {
        get { lock (_lock) return _brush; }
    }

    // 实际可抓取的屏幕区域（窗口矩形与显示器求交后的结果）
    private int _capX, _capY, _capW, _capH;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            LastExcludeError = 0;
            SelfExcluded = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            if (!SelfExcluded) LastExcludeError = Marshal.GetLastWin32Error();
        }
        catch { SelfExcluded = false; }
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
            // 只有窗口矩形真的变了才重抓。
            // dip 尺寸（DPI 缩放）变化只影响映射比例，不必触发一次昂贵且会闪的抓屏。
            var rectChanged = r.Left != _reqX || r.Top != _reqY || w != _reqW || h != _reqH;
            if (rectChanged)
            {
                _lastRequestAt = DateTime.UtcNow;
                ApplyRect(r.Left, r.Top, w, h, dipWidth, dipHeight);
                _dirty = true;
            }
            else
            {
                _reqDipW = dipWidth;
                _reqDipH = dipHeight;
            }
        }
        EnsureWorker();
    }

    /// <summary>按窗口矩形算出可抓取的屏幕区域（裁剪到显示器内）。调用方需持有 _lock。</summary>
    private void ApplyRect(int left, int top, int w, int h, double dipWidth, double dipHeight)
    {
        _reqX = left; _reqY = top; _reqW = w; _reqH = h;
        _reqDipW = dipWidth; _reqDipH = dipHeight;

        // 窗口有一侧可能伸出屏幕外（贴边锚定时 Top = -WindowPad 等）。
        // BitBlt 对屏幕外区域不会写入，位图那块会保留未初始化/上一次的像素，
        // 表现为「玻璃层残留上一帧像素」。这里先裁剪到显示器范围，
        // 只抓真正可见的部分，再按偏移映射回窗口坐标系。
        var monLeft = 0; var monTop = 0; var monRight = 0; var monBottom = 0;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (GetMonitorInfo(MonitorFromWindow(_hwnd, 2), ref mi) &&
            mi.rcMonitor.Right > mi.rcMonitor.Left)
        {
            monLeft = mi.rcMonitor.Left; monTop = mi.rcMonitor.Top;
            monRight = mi.rcMonitor.Right; monBottom = mi.rcMonitor.Bottom;
        }
        else
        {
            monRight = left + w; monBottom = top + h;
        }

        var cl = Math.Max(left, monLeft);
        var ct = Math.Max(top, monTop);
        var cr = Math.Min(left + w, monRight);
        var cb = Math.Min(top + h, monBottom);
        _capX = cl; _capY = ct;
        _capW = Math.Max(0, cr - cl);
        _capH = Math.Max(0, cb - ct);
    }

    /// <summary>
    /// 立即同步抓一次背景（不做 cloak）。
    /// 用于窗口尚未显示时（启动阶段）——此时抓到的必然是纯桌面，
    /// 既没有自身鬼影，也不会有任何闪烁。
    /// </summary>
    public bool CaptureNow(double dipWidth, double dipHeight)
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(_hwnd, out var r)) return false;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;

        int x, y, cw, ch;
        double dw, dh, offX, offY, winW, winH;
        lock (_lock)
        {
            ApplyRect(r.Left, r.Top, w, h, dipWidth, dipHeight);
            x = _capX; y = _capY; cw = _capW; ch = _capH;
            dw = _reqDipW; dh = _reqDipH;
            offX = _capX - _reqX; offY = _capY - _reqY;
            winW = _reqW; winH = _reqH;
            _dirty = false;
        }
        if (cw <= 0 || ch <= 0) return false;

        var source = Capture(x, y, cw, ch);
        if (source == null) return false;
        var brush = MakeBrush(source, offX, offY, cw, ch, winW, winH, dw, dh);
        if (brush == null) return false;
        lock (_lock) { _brush = brush; }
        CaptureCount++;
        return true;
    }

    /// <summary>标记内容需要刷新（窗口移动/展开时刷新桌面背景）。</summary>
    public void Invalidate()
    {
        _lastRequestAt = DateTime.UtcNow;
        _dirty = true;
    }

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
            // 只在“需要且几何已稳定”时抓一次。
            // 背景是窗口身后的桌面，与岛内内容无关 —— 滚动/动画期间完全不必重抓，
            // 这既消除了自身鬼影，也省掉了大部分 CPU。
            if (!_dirty) { Thread.Sleep(60); continue; }

            if ((DateTime.UtcNow - _lastRequestAt).TotalMilliseconds < SettleMs)
            {
                Thread.Sleep(30);
                continue;
            }

            int x, y, w, h;
            double dw, dh, offX, offY, winW, winH;
            lock (_lock)
            {
                if (!_dirty) continue;
                _dirty = false;
                x = _capX; y = _capY; w = _capW; h = _capH; dw = _reqDipW; dh = _reqDipH;
                offX = _capX - _reqX; offY = _capY - _reqY;
                winW = _reqW; winH = _reqH;
            }
            _lastCapture = DateTime.UtcNow;
            if (w <= 0 || h <= 0) continue;

            var source = CaptureCloaked(x, y, w, h);
            if (source == null) continue;

            var brush = MakeBrush(source, offX, offY, w, h, winW, winH, dw, dh);
            if (brush == null) continue;
            lock (_lock)
            {
                _brush = brush;
            }
            CaptureCount++;
        }
    }

    /// <summary>把窗口从合成画面中临时摘掉（DWM cloak），否则抓屏会拍到自身。</summary>
    private bool Cloak(bool on)
    {
        if (_hwnd == IntPtr.Zero) return false;
        try
        {
            var value = on ? 1 : 0;
            if (DwmSetWindowAttribute(_hwnd, DwmwaCloak, ref value, sizeof(int)) != 0) return false;
            DwmFlush();   // 等这一帧合成完成，确保抓到的画面里已经没有自己
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 抓屏。抓之前先 cloak 自己、抓完取消 cloak，
    /// 这样玻璃里只会是真正的桌面内容，不会含自身上一帧 → 无残影。
    /// </summary>
    private BitmapSource? CaptureCloaked(int x, int y, int w, int h)
    {
        var cloaked = CloakWhenCapturing && Cloak(true);
        try
        {
            return Capture(x, y, w, h);
        }
        finally
        {
            if (cloaked) Cloak(false);
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

    private static ImageBrush? MakeBrush(BitmapSource source, double offX, double offY,
        int capPixelW, int capPixelH, double winPixelW, double winPixelH, double dipW, double dipH)
    {
        try
        {
            // 物理像素 → DIP 的缩放比（高 DPI 下不等于 1）
            var sx = winPixelW > 0 ? dipW / winPixelW : 1.0;
            var sy = winPixelH > 0 ? dipH / winPixelH : 1.0;

            var brush = new ImageBrush(source)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, source.PixelWidth, source.PixelHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                // 只覆盖实际抓到的屏幕内区域；屏幕外部分留空，由主题色调填上
                Viewport = new Rect(
                    Math.Max(0, offX) * sx,
                    Math.Max(0, offY) * sy,
                    Math.Max(1, capPixelW * sx),
                    Math.Max(1, capPixelH * sy)),
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