using System;
using System.IO;

namespace Wisland.Tests;

/// <summary>
/// 测试用临时目录。
/// 不用 %TEMP%：部分受限环境（沙箱/组策略）不允许在系统临时目录建目录，
/// 统一放在测试程序集输出目录下的 test-tmp 里，行为稳定且清理简单。
/// </summary>
internal static class TestPaths
{
    public static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
