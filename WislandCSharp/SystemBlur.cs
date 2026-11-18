using System;
using System.Runtime.InteropServices;

namespace Wisland;

/// <summary>
/// 系统级背景模糊（DWM 亚克力/高斯）。
///
/// 实测结论（Windows 10 19045）：
///   1. **分层窗口**（AllowsTransparency=True）也能吃到 accent 模糊 —— 不必放弃逐像素 alpha
///   2. 模糊只出现在**窗口内有 alpha 的像素**处；alpha=0 的区域保持全透明，
///      不会在整个窗口矩形上留下模糊块 → 不需要 SetWindowRgn 形状化
///   3. 因此可以保留抗锯齿圆角、胶囊外阴影、边缘流光
///
/// 对比抓屏方案：无需截屏、无自捕获鬼影、无浅层/深层刷新问题、CPU 开销几乎为零。
/// </summary>
public static class SystemBlur
{
    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WcaAccentPolicy = 19;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public int dwFlags;
        public int fEnable;          // BOOL
        public IntPtr hRgnBlur;
        public int fTransitionOnMaximized;
    }

    private const int DwmBbEnable = 0x1;
    private const int DwmBbBlurRegion = 0x2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND bb);

    private static IntPtr _blurRgn = IntPtr.Zero;

    /// <summary>
    /// 给 DWM 指定"模糊区域"（窗口坐标）。
    /// 用来把窗口矩形级的 accent 模糊限制在胶囊形状内，
    /// 否则窗口四周边距也会被模糊，表现为药丸外多出一圈亮矩形。
    /// 传 IntPtr.Zero 表示取消区域限制。
    /// </summary>
    public static bool SetBlurRegion(IntPtr hwnd, IntPtr region)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var bb = new DWM_BLURBEHIND
            {
                dwFlags = DwmBbEnable | (region != IntPtr.Zero ? DwmBbBlurRegion : 0),
                fEnable = 1,
                hRgnBlur = region,
                fTransitionOnMaximized = 0,
            };
            var hr = DwmEnableBlurBehindWindow(hwnd, ref bb);
            if (hr != 0) return false;
            _blurRgn = region;
            return true;
        }
        catch { return false; }
    }

    /// <summary>当前是否已启用（用于调试面板显示）。</summary>
    public static bool Enabled { get; private set; }

    /// <summary>最后一次调用的返回值。</summary>
    public static bool LastCallOk { get; private set; }

    /// <summary>
    /// 开启/关闭系统背景模糊。
    /// </summary>
    /// <param name="hwnd">岛体窗口句柄</param>
    /// <param name="on">是否启用</param>
    /// <param name="acrylic">true = 亚克力（带噪点纹理），false = 经典高斯模糊</param>
    /// <param name="tintAbgr">叠加色调（ABGR，alpha 建议 0x20~0x60，太大看不到模糊）</param>
    public static bool Apply(IntPtr hwnd, bool on, bool acrylic = false, int tintAbgr = 0x00000000, int accentFlags = 0)
    {
        if (hwnd == IntPtr.Zero) return false;

        var state = !on
            ? AccentState.Disabled
            : (acrylic ? AccentState.EnableAcrylicBlurBehind : AccentState.EnableBlurBehind);

        var policy = new AccentPolicy
        {
            AccentState = (int)state,
            AccentFlags = accentFlags,
            GradientColor = tintAbgr,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf(typeof(AccentPolicy));
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = ptr,
                SizeOfData = size,
            };
            var ok = SetWindowCompositionAttribute(hwnd, ref data) != 0;
            LastCallOk = ok;
            Enabled = on;
            return ok;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
