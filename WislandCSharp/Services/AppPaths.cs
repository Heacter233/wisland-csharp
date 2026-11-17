using System;
using System.IO;

namespace Wisland.Services;

/// <summary>应用常用路径（配置、数据目录、日志）。</summary>
public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;

    public static string ConfigFile => Path.Combine(BaseDir, "config.json");

    public static string DataDir => Path.Combine(BaseDir, "data");

    public static string CrashLog => Path.Combine(DataDir, "crash.log");

    public static string DebugLog => Path.Combine(DataDir, "debug.log");
}
