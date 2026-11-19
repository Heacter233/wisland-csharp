using System;
using System.IO;

namespace Wisland.Services;

/// <summary>应用常用路径（配置、数据目录、日志）。</summary>
public static class AppPaths
{
    private static string? _baseDir;

    /// <summary>
    /// 程序目录。
    /// 单文件自包含发布时 <see cref="AppContext.BaseDirectory"/> 指向临时解包目录，
    /// 配置/数据/插件写到那里会在退出后丢失，所以优先用 <see cref="Environment.ProcessPath"/> 的真实位置。
    /// </summary>
    public static string BaseDir => _baseDir ??= ResolveBaseDir();

    private static string ResolveBaseDir()
    {
        try
        {
            var proc = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(proc))
            {
                var name = Path.GetFileNameWithoutExtension(proc);
                // 通过 `dotnet Wisland.dll` 启动时 ProcessPath 是 dotnet.exe，此时用 AppContext 更可靠
                if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(proc);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir!;
                }
            }
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Resources 下的文件：优先 exe 旁边（轻量版），其次程序基目录（单文件版的解包目录）。
    /// </summary>
    public static string ResourcePath(string fileName)
    {
        try
        {
            var beside = Path.Combine(BaseDir, "Resources", fileName);
            if (File.Exists(beside)) return beside;
        }
        catch { }
        return Path.Combine(AppContext.BaseDirectory, "Resources", fileName);
    }

    public static string ConfigFile => Path.Combine(BaseDir, "config.json");

    public static string DataDir => Path.Combine(BaseDir, "data");

    public static string CrashLog => Path.Combine(DataDir, "crash.log");

    public static string DebugLog => Path.Combine(DataDir, "debug.log");
}
