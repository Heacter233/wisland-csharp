using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Wisland.Services;

/// <summary>
/// 全局热键服务接口：注册系统全局热键，分发热键事件。
/// 对应 Python island.py 中的热键注册（RegisterHotKey）。
/// </summary>
public interface IHotkeyService
{
    /// <summary>
    /// 已注册热键字典：热键 ID -> 热键名称
    /// </summary>
    IReadOnlyDictionary<int, string> RegisteredHotkeys { get; }

    /// <summary>
    /// 热键按下事件：参数为 (sender, 热键名称)
    /// </summary>
    event EventHandler<string>? HotkeyPressed;

    /// <summary>
    /// 绑定窗口句柄（必须在注册热键前调用）
    /// </summary>
    void Initialize(IntPtr hwnd);

    /// <summary>
    /// 注册热键
    /// </summary>
    /// <param name="name">热键名称，用于分发事件</param>
    /// <param name="modifiers">修饰键：Control=2, Alt=1, Shift=4, Win=8</param>
    /// <param name="key">虚拟键码</param>
    /// <returns>是否注册成功</returns>
    bool Register(string name, uint modifiers, uint key);

    /// <summary>
    /// 注销热键
    /// </summary>
    /// <param name="name">热键名称</param>
    void Unregister(string name);

    /// <summary>
    /// 注销所有热键
    /// </summary>
    void UnregisterAll();

    /// <summary>
    /// 由 WndProc 调用：处理 WM_HOTKEY 消息
    /// </summary>
    void OnHotkey(IntPtr wParam);
}

/// <summary>
/// 全局热键服务实现
/// </summary>
public class HotkeyService : IHotkeyService
{
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly Dictionary<int, string> _hotkeys = new();
    private int _nextId = 1;

    public event EventHandler<string>? HotkeyPressed;
    public IReadOnlyDictionary<int, string> RegisteredHotkeys => _hotkeys;

    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public bool Register(string name, uint modifiers, uint key)
    {
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("HotkeyService must be initialized with a window handle before registering hotkeys.");

        // 检查是否已注册同名
        foreach (var kvp in _hotkeys)
        {
            if (kvp.Value == name)
                Unregister(name);
        }

        var id = _nextId++;

        // MOD_NOREPEAT：按住热键不连发。
        // 不加的话按住 Ctrl+Alt+W 会以系统按键重复率疯狂触发 toggle（岛在展开/收起之间抽搐）。
        // 该标志自 Windows 7 起支持。
        const uint ModNoRepeat = 0x4000;
        bool success = NativeMethods.RegisterHotKey(_hwnd, id, modifiers | ModNoRepeat, key);
        if (success)
        {
            _hotkeys[id] = name;
            return true;
        }

        // 注册失败要能被看到：被别的程序占用是最常见原因，静默失败会让用户以为热键没生效
        var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        LastError = err == 1409
            ? $"热键 {name} 注册失败：已被其它程序占用（Win32 1409）"
            : $"热键 {name} 注册失败（Win32 {err}）";
        try { App.DebugNote("hotkey: " + LastError); } catch { }
        return false;
    }

    /// <summary>
    /// 解析 "Ctrl+Alt+W" / "Shift+F5" 这类配置字符串，返回 (修饰键, 虚拟键码)。
    /// 从 MainWindow 挪到服务里，纯逻辑，便于单测。
    /// </summary>
    public static (uint Mod, uint Vk) ParseHotkey(string s)
    {
        var parts = (s ?? "").Split('+');
        uint mod = 0, vk = 0;
        foreach (var raw in parts)
        {
            var p = raw.Trim().ToLowerInvariant();
            switch (p)
            {
                case "ctrl": case "control": mod |= 0x0002; break;
                case "alt": mod |= 0x0001; break;
                case "shift": mod |= 0x0004; break;
                case "win": case "windows": mod |= 0x0008; break;
                case "space": vk = 0x20; break;
                case "esc": case "escape": vk = 0x1B; break;
                case "enter": case "return": vk = 0x0D; break;
                case "tab": vk = 0x09; break;
                default:
                    if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = (uint)char.ToUpperInvariant(p[0]);
                    else if (p.Length is 2 or 3 && p[0] == 'f' && int.TryParse(p[1..], out var n)
                             && n is >= 1 and <= 24)
                        vk = (uint)(0x70 + n - 1);
                    break;
            }
        }
        return (mod, vk);
    }

    /// <summary>最近一次注册失败的原因（null = 没有失败）；设置页/主窗口据此提示。</summary>
    public string? LastError { get; private set; }

    public void Unregister(string name)
    {
        var idsToRemove = new List<int>();
        foreach (var kvp in _hotkeys)
        {
            if (kvp.Value == name)
            {
                NativeMethods.UnregisterHotKey(_hwnd, kvp.Key);
                idsToRemove.Add(kvp.Key);
            }
        }
        foreach (var id in idsToRemove)
            _hotkeys.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotkeys.Keys)
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
    }

    public void OnHotkey(IntPtr wParam)
    {
        var id = wParam.ToInt32();
        if (_hotkeys.TryGetValue(id, out var name))
        {
            HotkeyPressed?.Invoke(this, name);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

/// <summary>
/// 修饰键常量
/// </summary>
public static class HotkeyModifiers
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
}

/// <summary>
/// 常用虚拟键码
/// </summary>
public static class VirtualKeys
{
    public const uint VK_W = 0x57;
    public const uint VK_I = 0x49;
    public const uint VK_C = 0x43;
    public const uint VK_J = 0x4A;
    public const uint VK_V = 0x56;
}