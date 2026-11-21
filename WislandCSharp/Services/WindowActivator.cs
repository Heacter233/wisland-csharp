using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Wisland.Services;

/// <summary>
/// 把已经在运行（可能最小化 / 只在托盘）的程序主窗口唤到前台并给焦点。
/// 用于消息磁贴点击：进程在跑 → 唤出窗口；没跑 → 才启动。
/// </summary>
public static class WindowActivator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool ok);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SwShow = 5;
    private const int SwRestore = 9;
    private static readonly List<string> _diag = new();

    /// <summary>与目标程序无关的窗口（输入法、桌面、任务栏等）。</summary>
    private static bool IsJunkWindow(string cls, string title)
    {
        if (cls.Contains("IME", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.Contains("MSCTF", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls is "Shell_TrayWnd" or "Progman" or "WorkerW" or "Shell_SecondaryTrayWnd") return true;
        // 注意：这里必须对列出的标题返回 true。原实现写成 `return title == ""`，
        // 于是"Default IME / MSCTFIME UI / Program Manager"这些窗口反而被当成有效目标。
        if (title is "Default IME" or "MSCTFIME UI" or "Program Manager") return true;
        return false;
    }

    /// <summary>最近一次枚举到的候选窗口（排查用）。</summary>
    public static string Diagnostics => _diag.Count == 0 ? "(none)" : string.Join(" ;; ", _diag);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

    private static string GetClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        try { GetClassName(h, sb, sb.Capacity); } catch { }
        return sb.ToString();
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>
    /// 尝试把指定进程名的主窗口唤到前台。成功返回 true（并输出窗口句柄与标题）。
    /// </summary>
    public static bool TryActivate(string[] exeNames, out string detail)
        => TryActivate(exeNames, Array.Empty<string>(), out detail);

    public static bool TryActivate(string[] exeNames, string[] keywords, out string detail)
    {
        detail = "";
        var pids = new HashSet<uint>();
        foreach (var exe in exeNames)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(exe);
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!p.HasExited) pids.Add((uint)p.Id);
                    }
                    catch { }
                }
            }
            catch { }
        }
        if (pids.Count == 0) { detail = "no-process"; return false; }

        _diag.Clear();
        var windows = new List<(IntPtr Hwnd, int Score, string Title)>();
        var shell = GetShellWindow();
        var desktop = GetDesktopWindow();

        EnumWindows((h, _) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return true;
            if (h == shell || h == desktop) return true;

            var ex = GetWindowLong(h, GwlExStyle);
            var cls = GetClassName(h);

            var len = GetWindowTextLength(h);
            var sb = new StringBuilder(Math.Max(4, len + 1));
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();

            GetWindowRect(h, out var r);
            var area = Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            var visible = IsWindowVisible(h);
            var tool = (ex & WsExToolWindow) != 0;

            _diag.Add($"0x{h.ToInt64():X} cls={cls} vis={visible} tool={tool} area={area} title='{title}'");

            // 排除 IME / 桌面 / 任务栏之类与目标程序无关的窗口
            // （曾把 QQ 的 "Default IME" 当成主窗口而"唤出"成功，实际什么也没发生）
            if (IsJunkWindow(cls, title)) return true;

            // 可见 + 有标题 + 面积大的优先；工具窗口和零面积窗口降权但不直接丢弃
            // （QQNT/微信有些主窗口带 WS_EX_TOOLWINDOW，或最小化到托盘时面积为 0）
            // 至少要"有标题"或"有面积"，否则是 Electron/框架的辅助窗口
            if (area <= 0 && string.IsNullOrWhiteSpace(title)) return true;
            // Chrome_WidgetWin_0 这类是 GPU/辅助窗口，不是主界面
            if (cls.Equals("Chrome_WidgetWin_0", StringComparison.OrdinalIgnoreCase)) return true;

            var score = area;
            if (visible) score += 1_000_000;
            if (!string.IsNullOrWhiteSpace(title)) score += 500_000;
            if (tool) score -= 900_000;
            windows.Add((h, score, title));
            return true;
        }, IntPtr.Zero);

        // 兜底：按窗口标题关键字匹配。
        // QQ 的主界面窗口可能属于 QQEdit 这类兄弟进程（不在 exeNames 里），
        // 只按进程名找会漏掉，导致"运行中却唤不出来"。
        if (windows.Count == 0 && keywords.Length > 0)
        {
            EnumWindows((h, _) =>
            {
                if (h == shell || h == desktop) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!IsWindowVisible(h)) return true;

                var len = GetWindowTextLength(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;
                if (!keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;

                var cls = GetClassName(h);
                if (IsJunkWindow(cls, title)) return true;

                GetWindowRect(h, out var r);
                var area = Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
                windows.Add((h, 2_000_000 + area, title));
                return true;
            }, IntPtr.Zero);
            if (windows.Count > 0) detail = "by-keyword:" + string.Join(",", keywords);
        }

        if (windows.Count == 0) { detail = "no-window"; return false; }

        var best = windows.OrderByDescending(w => w.Score).First();
        var hwnd = best.Hwnd;

        try
        {
            // 托盘状态：窗口可能是"隐藏"而不是"最小化"，两种都试
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            else { ShowWindow(hwnd, SwShow); ShowWindow(hwnd, SwRestore); }

            // 临时置顶拿焦点，之后要还原：原实现置顶后从不恢复，
            // 被唤出的 QQ/微信会永久盖在别的窗口上（用户表现为"点一次磁贴之后它就一直在最前"）。
            var wasTopmost = (GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) != 0;

            SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);

            if (!SetForegroundWindow(hwnd))
            {
                // 前台锁定：临时把输入队列挂到目标线程上再抢焦点
                var fg = GetForegroundWindow();
                var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
                var targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                if (fgThread != 0 && targetThread != 0 && fgThread != targetThread)
                {
                    AttachThreadInput(fgThread, targetThread, true);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(fgThread, targetThread, false);
                }
                else
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
            }

            var nowVisible = IsWindowVisible(hwnd);
            var isFg = GetForegroundWindow() == hwnd;

            // 还原置顶状态（原本不是置顶的才降回去）
            if (!wasTopmost)
            {
                try
                {
                    SetWindowPos(hwnd, (IntPtr)(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0,
                        SwpNoMove | SwpNoSize | SwpNoActivate);
                }
                catch { }
            }

            GetWindowRect(hwnd, out var rr);
            var nowArea = Math.Max(0, rr.Right - rr.Left) * Math.Max(0, rr.Bottom - rr.Top);
            detail = (detail.Length > 0 ? detail + " " : "")
                     + $"hwnd=0x{hwnd.ToInt64():X} title='{best.Title}' visible={nowVisible} fg={isFg} area={nowArea}";
            return isFg || (nowVisible && nowArea > 0);
        }
        catch (Exception ex)
        {
            detail = "error:" + ex.Message;
            return false;
        }
    }
}