using System;
using System.IO;
using System.Text;

namespace Wisland.Services;

/// <summary>
/// 带轮转的文本日志追加。
///
/// 仓里的 app-launch.log / debug.log / crash.log 原来只追加不轮转，
/// 长期运行会无限增长（插件日志早就有 512KB 轮转，这里补齐）。
/// 轮转策略：单个文件超过 <see cref="MaxBytes"/> 时改名成 <c>xxx.1.log</c>
/// （只保留一份历史，不堆成无穷多个文件）。
/// </summary>
internal static class RotatingLog
{
    /// <summary>单文件上限（字节）。</summary>
    private const long MaxBytes = 1024 * 1024;      // 1 MB

    /// <summary>追加一行文本；失败静默（日志本身不应该影响主流程）。</summary>
    public static void Append(string path, string text, Encoding? encoding = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            RotateIfNeeded(path);

            var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.AppendAllText(path, text, enc);
        }
        catch { }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;

            var backup = Path.Combine(
                fi.DirectoryName ?? ".",
                Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));

            // 覆盖上一份历史（File.Move overwrite 在旧框架没有，先删再移）
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            File.Move(path, backup);
        }
        catch { }
    }
}
