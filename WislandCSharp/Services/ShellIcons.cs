using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisland.Services;

/// <summary>
/// 取"程序 / 文件 / 文件夹"的系统图标（启动栏用），并支持自定义图标文件。
/// 结果按路径缓存，避免每次重建启动栏都去问 shell。
/// </summary>
public static class ShellIcons
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attr,
        ref SHFILEINFO info, uint cbInfo, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取图标：优先自定义图标文件，否则用系统图标（文件夹也给文件夹图标）。</summary>
    public static ImageSource? Get(string path, bool isFolder, string customIcon = "")
    {
        var key = (isFolder ? "D|" : "F|") + path + "|" + customIcon;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        ImageSource? img = null;

        // 1) 自定义图标文件
        if (!string.IsNullOrWhiteSpace(customIcon) && File.Exists(customIcon))
            img = LoadImageFile(customIcon);

        // 2) 系统图标（shell 关联图标）
        if (img == null)
            img = FromShell(path, isFolder);

        Cache[key] = img;
        return img;
    }

    private const uint ShgfiDisplayName = 0x00000200;

    /// <summary>shell 的本地化显示名（例如 Desktop → 桌面）；取不到返回空。</summary>
    private static string ShellDisplayName(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            uint flags = ShgfiDisplayName;
            uint attr = 0;
            if (Directory.Exists(path) || File.Exists(path))
            {
                // 真实存在：让 shell 直接解析
            }
            else
            {
                flags |= ShgfiUseFileAttributes;
                attr = FileAttributeDirectory;
            }
            SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            return info.szDisplayName ?? "";
        }
        catch { return ""; }
    }

    private static ImageSource? LoadImageFile(string file)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static ImageSource? FromShell(string path, bool isFolder)
    {
        try
        {
            var info = new SHFILEINFO();
            uint flags = ShgfiIcon | ShgfiLargeIcon;
            uint attr = 0;
            var target = path;

            if (isFolder && !Directory.Exists(path))
            {
                // 文件夹已不存在时也要能显示文件夹图标
                flags |= ShgfiUseFileAttributes;
                attr = FileAttributeDirectory;
            }
            else if (!File.Exists(path) && !Directory.Exists(path))
            {
                flags |= ShgfiUseFileAttributes;
                attr = FileAttributeNormal;
            }

            var r = SHGetFileInfo(target, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }

    /// <summary>文件夹的显示名（不带路径，优先 shell 本地化名称），用于 ToolTip / 名称回退。</summary>
    public static string DisplayName(string path, bool isFolder)
    {
        try
        {
            if (isFolder || Directory.Exists(path))
            {
                var shellName = ShellDisplayName(path);
                if (!string.IsNullOrWhiteSpace(shellName)) return shellName;

                var trimmed = path.TrimEnd('\\', '/');
                var name = Path.GetFileName(trimmed);
                return string.IsNullOrWhiteSpace(name) ? trimmed : name;
            }
            var f = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(f) ? Path.GetFileName(path) : f;
        }
        catch { return path; }
    }
}