using System;
using System.Collections.Generic;
using System.IO;

namespace Wisland.Plugins;

/// <summary>
/// 插件程序集的"影子副本"。
///
/// 为什么要复制：热重载时宿主需要先卸载旧程序集、再重新加载新文件。
/// 但 .NET Core 的 AssemblyLoadContext.Unload 只是"尽力"回收 ——
/// 只要插件还有活着的线程/定时器/静态引用，程序集就仍被占用，
/// 源目录里的 dll 处于文件锁状态，重新加载或替换文件都会失败。
///
/// 所以加载时把插件文件复制到 data/plugins/.shadow/&lt;id&gt;/&lt;序号&gt;/ 再加载，
/// 源文件永远不被锁定：用户可以随时覆盖/替换，重载时换一个新的影子目录即可。
/// 旧的影子目录在下次启动时清理（若仍被占用则跳过，不影响使用）。
/// </summary>
internal static class PluginShadow
{
    private static int _seq;

    /// <summary>复制插件到新的影子目录，返回该目录；失败返回 null 并给出原因。</summary>
    public static string? Prepare(PluginManifest manifest, string shadowRoot, out string error)
    {
        error = "";
        try
        {
            var idDir = Path.Combine(shadowRoot, manifest.Id);
            var n = System.Threading.Interlocked.Increment(ref _seq);
            var target = Path.Combine(idDir, $"{DateTime.Now:HHmmss}_{n}");

            Directory.CreateDirectory(target);
            CopyTree(manifest.Directory, target);

            if (!File.Exists(Path.Combine(target, manifest.Assembly)))
            {
                error = $"影子副本里缺少程序集 {manifest.Assembly}";
                return null;
            }

            // 清掉该插件旧的影子目录（尽量；被占用就留给下次启动）
            TryCleanupChildren(idDir, keep: target);
            return target;
        }
        catch (Exception ex)
        {
            error = "创建影子副本失败：" + ex.Message;
            return null;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            // 跳过日志/配置/缓存类文件，避免把用户数据复制进影子
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(target, name);
            try { File.Copy(file, dest, overwrite: true); } catch { }
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var sub = Path.GetFileName(dir);
            CopyTree(dir, Path.Combine(target, sub));
        }
    }

    /// <summary>启动时整体清理（上一轮进程遗留的影子目录）。</summary>
    public static void CleanupAll(string shadowRoot)
    {
        try
        {
            if (!Directory.Exists(shadowRoot)) return;
            foreach (var idDir in Directory.GetDirectories(shadowRoot))
                TryCleanupChildren(idDir, keep: null);
        }
        catch { }
    }

    private static void TryCleanupChildren(string dir, string? keep)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var child in Directory.GetDirectories(dir))
            {
                if (keep != null && string.Equals(
                        Path.GetFullPath(child), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(child, recursive: true); } catch { }
            }
        }
        catch { }
    }
}
