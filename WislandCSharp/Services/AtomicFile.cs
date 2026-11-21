using System;
using System.IO;
using System.Text;

namespace Wisland.Services;

/// <summary>
/// 原子落盘：先写临时文件并刷盘，再整体替换目标文件。
///
/// 为什么必须这样：<c>File.WriteAllText</c> 是"先截断再写入"，
/// 进程在写入瞬间被终止（崩溃、断电、任务管理器结束）就会留下半截文件。
/// 对 config.json 这种"一坏就全丢"的文件，代价是用户全部设置（含 API Key）。
///
/// 策略：
///   1. 写 <c>xxx.tmp</c>（Flush(true) 保证内容落到磁盘而不是只进系统缓存）；
///   2. 目标存在 → <c>File.Replace</c> 原子替换，并保留一份 <c>xxx.bak</c>；
///      目标不存在 → <c>File.Move</c>；
///   3. <c>File.Replace</c> 在部分文件系统/占用场景会失败，退化为"删除 + 移动"，
///      再失败才报错（错误由调用方转成可见提示，不再静默吞掉）。
/// </summary>
public static class AtomicFile
{
    /// <summary>备份后缀：最后一次成功写入前的版本。</summary>
    public const string BackupSuffix = ".bak";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 原子写入文本。失败时返回 false 并给出原因（不抛异常，调用方负责提示）。
    /// </summary>
    public static bool WriteAllText(string path, string text, out string? error)
    {
        error = null;
        var tmp = path + ".tmp";
        var bak = path + BackupSuffix;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 1) 写临时文件并强制刷盘
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // 2) 原子替换（保留 .bak）
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                    return true;
                }
                catch (Exception ex)
                {
                    // 某些文件系统（网络盘 / 某些同步盘）不支持 Replace，退化为两步
                    error = ex.Message;
                }
            }

            // 3) 退化路径：直接覆盖
            try
            {
                File.Copy(tmp, path, overwrite: true);
                TryDelete(tmp);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TryDelete(tmp);
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDelete(tmp);
            return false;
        }
    }

    /// <summary>
    /// 处理损坏文件：改名保留为 <c>文件名.corrupt-时间戳</c>，并尝试用 .bak 恢复。
    /// 返回保留路径（无损坏/改名失败时为 null）与是否成功从备份恢复。
    /// </summary>
    public static (string? QuarantinePath, bool RestoredFromBackup) Quarantine(string path)
    {
        string? quarantine = null;
        try
        {
            if (File.Exists(path))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                quarantine = $"{path}.corrupt-{stamp}";
                File.Move(path, quarantine, overwrite: true);
            }
        }
        catch
        {
            // 改名失败（被占用）：保留原文件，至少不要覆盖它
            return (null, false);
        }

        try
        {
            var bak = path + BackupSuffix;
            if (File.Exists(bak))
            {
                File.Copy(bak, path, overwrite: true);
                return (quarantine, true);
            }
        }
        catch { }

        return (quarantine, false);
    }

    /// <summary>目录是否可写（用于启动时给出"设置永远保存不了"的可见提示）。</summary>
    public static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
