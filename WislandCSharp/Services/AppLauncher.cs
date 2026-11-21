using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Wisland.Services;

/// <summary>
/// 启动 / 唤出外部程序（QQ、微信、邮件）的兜底逻辑。
///
/// 背景：只靠 URI 协议（tencent:// / weixin://）在很多机器上根本没注册，
/// 点击磁贴会毫无反应。这里按优先级逐级尝试：
///   1. config.json 里配置的 notify.open.&lt;kind&gt; 路径
///   2. 正在运行的进程（取其可执行文件路径，单实例程序会自己激活窗口）
///   3. 常见安装路径
///   4. 开始菜单快捷方式（.lnk，按关键字匹配）
///   5. 卸载注册表的 InstallLocation 里搜可执行文件
///   6. 最后的 URI 协议兜底
/// 每次结果都写到 data/app-launch.log，方便排查。
/// </summary>
public static class AppLauncher
{
    private sealed record Target(
        string Kind,
        string[] ExeNames,
        string[] Keywords,
        string[] KnownPaths,
        string Uri);

    /// <summary>启动后是否再唤出一次（拿焦点）。</summary>
    private static bool _focusAfterLaunch = true;

    private static readonly Target[] Targets =
    {
        new("qq",
            new[] { "QQ.exe", "QQNT.exe", "QQScLauncher.exe" },
            new[] { "QQ", "腾讯QQ", "QQNT" },
            new[]
            {
                @"%ProgramFiles%\Tencent\QQNT\QQ.exe",
                @"%ProgramFiles%\Tencent\QQ\Bin\QQ.exe",
                @"%ProgramFiles(x86)%\Tencent\QQ\Bin\QQ.exe",
                @"%LOCALAPPDATA%\Tencent\QQNT\QQ.exe",
                @"%ProgramFiles%\Tencent\QQNT\QQScLauncher.exe",
            },
            "tencent://"),

        new("wechat",
            new[] { "Weixin.exe", "WeChat.exe", "WeChatApp.exe" },
            new[] { "微信", "WeChat", "Weixin" },
            new[]
            {
                @"%ProgramFiles%\Tencent\Weixin\Weixin.exe",
                @"%ProgramFiles%\Tencent\WeChat\WeChat.exe",
                @"%ProgramFiles(x86)%\Tencent\WeChat\WeChat.exe",
                @"%LOCALAPPDATA%\Tencent\WeChat\WeChat.exe",
            },
            "weixin://"),

        new("mail",
            new[] { "thunderbird.exe", "outlook.exe", "Foxmail.exe" },
            new[] { "Thunderbird", "Outlook", "Foxmail", "邮件" },
            new[]
            {
                @"%ProgramFiles%\Mozilla Thunderbird\thunderbird.exe",
                @"%ProgramFiles(x86)%\Mozilla Thunderbird\thunderbird.exe",
            },
            "mailto:"),
    };

    /// <summary>
    /// 启动（或唤出）指定类型的程序；成功返回 true。
    ///
    /// 注意：内部有多段"等窗口出现"的轮询（最长约 8.5 秒），**必须异步调用**。
    /// 早期版本是同步方法，调用方又都在 UI 事件里，于是点一下消息磁贴就冻结界面 8 秒多。
    /// </summary>
    public static Task<bool> LaunchAsync(string kind, string? configuredPath = null)
        => Task.Run(() => LaunchCore(kind, configuredPath));

    private static bool LaunchCore(string kind, string? configuredPath)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        var target = Targets.FirstOrDefault(t => t.Kind == kind);
        Log(kind, "start", $"configured='{configuredPath}'");

        // 进程在跑 → 把它的窗口唤到前台并聚焦（最小化/托盘状态也要还原），不再重复启动
        if (target != null)
        {
            var probe = target.ExeNames.ToList();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                var cfgExe = Path.GetFileName(Expand(configuredPath));
                if (!string.IsNullOrWhiteSpace(cfgExe) && !probe.Contains(cfgExe, StringComparer.OrdinalIgnoreCase))
                    probe.Add(cfgExe);
            }
            if (WindowActivator.TryActivate(probe.ToArray(), target.Keywords, out var actDetail))
            {
                Log(kind, "ok:activated", actDetail);
                return true;
            }
            Log(kind, "miss:activate", actDetail + " | " + WindowActivator.Diagnostics);
        }

        // 1) 配置路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = Expand(configuredPath);
            if (File.Exists(p) && TryStart(p)) { Log(kind, "ok:config", p); FocusAfterLaunch(kind, target); return true; }
            Log(kind, "miss:config", p);
        }

        if (target == null)
        {
            Log(kind, "fail:unknown-kind", "");
            return false;
        }

        // 2) 正在运行的进程（单实例程序会自己把窗口提到前台）
        foreach (var exe in target.ExeNames)
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            Process? proc = null;
            try { proc = Process.GetProcessesByName(name).FirstOrDefault(); } catch { }
            if (proc == null) continue;
            string? path = null;
            try { path = proc.MainModule?.FileName; } catch { }
            if (!string.IsNullOrWhiteSpace(path) && TryStart(path!))
            {
                Log(kind, "ok:running", path!);
                RememberPath(kind, path!);
                FocusAfterLaunch(kind, target);
                return true;
            }
        }

        // 3) 已知安装路径
        foreach (var kp in target.KnownPaths)
        {
            var p = Expand(kp);
            if (File.Exists(p) && TryStart(p)) { Log(kind, "ok:known-path", p); RememberPath(kind, p); FocusAfterLaunch(kind, target); return true; }
        }

        // 4) 开始菜单快捷方式（排除卸载/帮助/更新之类的快捷方式，避免误启动卸载程序）
        var shortcuts = StartMenuShortcuts()
            .Where(lnk => !IsUnwantedShortcut(Path.GetFileNameWithoutExtension(lnk)))
            .OrderByDescending(lnk => ExactShortcutScore(Path.GetFileNameWithoutExtension(lnk), target.Keywords))
            .ToList();
        foreach (var lnk in shortcuts)
        {
            var fileName = Path.GetFileNameWithoutExtension(lnk);
            if (!target.Keywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

            // 优先直接启动快捷方式指向的 exe（微信可能装在 F:\ 之类的位置），
            // 快捷方式本身解析失败时再退回启动 .lnk
            var resolved = ResolveShortcutTarget(lnk);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved) && TryStart(resolved!))
            {
                Log(kind, "ok:shortcut-target", resolved!);
                RememberPath(kind, resolved!);
                FocusAfterLaunch(kind, target);
                return true;
            }
            if (TryStart(lnk)) { Log(kind, "ok:shortcut", lnk); return true; }
        }

        // 5) 卸载注册表的 InstallLocation
        foreach (var dir in InstallLocations(target.Keywords))
        {
            foreach (var exe in target.ExeNames)
            {
                var found = FindExe(dir, exe, depth: 2);
                if (found != null && TryStart(found)) { Log(kind, "ok:registry", found); RememberPath(kind, found); FocusAfterLaunch(kind, target); return true; }
            }
        }

        // 6) URI 协议兜底
        if (!string.IsNullOrEmpty(target.Uri) && TryStart(target.Uri))
        {
            Log(kind, "ok:uri", target.Uri);
            FocusAfterLaunch(kind, target);
            return true;
        }

        Log(kind, "fail:exhausted", "");
        return false;
    }

    /// <summary>启动后再等窗口出现并唤出一次，保证新启动的程序拿到焦点。</summary>
    private static void FocusAfterLaunch(string kind, Target target)
    {
        if (!_focusAfterLaunch) return;
        try
        {
            for (var i = 0; i < 15; i++)
            {
                System.Threading.Thread.Sleep(400);
                if (WindowActivator.TryActivate(target.ExeNames, target.Keywords, out var detail))
                {
                    Log(kind, "ok:activated-after-launch", detail);
                    return;
                }
            }
            Log(kind, "miss:activate-after-launch", "");
        }
        catch { }
    }

    /// <summary>把探测到的路径写回 config（notify.open.&lt;kind&gt;），下次直接命中第 1 级。</summary>
    private static void RememberPath(string kind, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path)) return;
            var cur = ServiceHub.Config.Get($"notify.open.{kind}", "");
            if (string.Equals(cur, path, StringComparison.OrdinalIgnoreCase)) return;
            ServiceHub.Config.Set($"notify.open.{kind}", path);
            Log(kind, "remember", path);
        }
        catch { }
    }

    private static bool TryStart(string target)
    {
        try
        {
            // 一律交给 shell 启动（UseShellExecute = true）。
            // 实测：微信 4.x 的 Weixin.exe 用 CreateProcess(UseShellExecute=false) 启动会立刻退出，
            // 走 shell 才能正常起来。启动后再轮询进程确认（拿不到句柄也能判断）。
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(target);
                for (var i = 0; i < 10; i++)
                {
                    System.Threading.Thread.Sleep(250);
                    try
                    {
                        var alive = Process.GetProcessesByName(name)
                            .Any(p => !p.HasExited);
                        if (alive) { Log("start", "exe-verified", $"{target} ({(i + 1) * 250}ms)"); return true; }
                    }
                    catch { }
                }
                Log("start", "exe-unverified", target);
                return true;   // shell 已接受，交给系统处理
            }

            Log("start", "shell-started", target);
            return true;
        }
        catch (Exception ex)
        {
            Log("start", "error", $"{target} -> {ex.Message}");
            return false;
        }
    }

    /// <summary>卸载 / 帮助 / 更新之类的快捷方式一律不要。</summary>
    private static bool IsUnwantedShortcut(string name) =>
        name.Contains("卸载", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("帮助", StringComparison.OrdinalIgnoreCase)
        || name.Contains("更新", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Update", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>快捷方式名与关键字越接近分数越高（"微信" &gt; "微信读书"）。</summary>
    private static int ExactShortcutScore(string name, string[] keywords)
    {
        var best = 0;
        foreach (var k in keywords)
        {
            if (string.Equals(name, k, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 3);
            else if (name.StartsWith(k, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 2);
            else if (name.Contains(k, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 1);
        }
        return best;
    }

    private static string Expand(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static IEnumerable<string> StartMenuShortcuts()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var f in SafeEnumerate(dir, "*.lnk", 4)) yield return f;
        }
    }

    /// <summary>
    /// 逐层自己递归：Directory.GetFiles(AllDirectories) 一旦遇到无权限子目录就整体抛异常、
    /// 返回空集合 —— 曾导致开始菜单里明明有微信快捷方式却完全找不到。
    /// </summary>
    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, int depth)
    {
        string[] files;
        try { files = Directory.GetFiles(dir, pattern); }
        catch { files = Array.Empty<string>(); }
        foreach (var f in files) yield return f;

        if (depth <= 0) yield break;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { yield break; }
        foreach (var sub in subs)
            foreach (var f in SafeEnumerate(sub, pattern, depth - 1))
                yield return f;
    }

    /// <summary>解析 .lnk 的目标路径（WScript.Shell，反射调用避免额外依赖）；失败返回 null。</summary>
    private static string? ResolveShortcutTarget(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            var shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            var shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            if (shortcut == null) return null;
            var target = shortcut.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    private static IEnumerable<string> InstallLocations(string[] keywords)
    {
        var roots = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var root in roots)
        {
            using var key = hive.OpenSubKey(root);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app == null) continue;
                var name = app.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!keywords.Any(k => name!.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                var loc = app.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc)) yield return loc!;
            }
        }
    }

    private static string? FindExe(string dir, string exeName, int depth)
    {
        try
        {
            var hit = Directory.GetFiles(dir, exeName, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (hit != null) return hit;
            if (depth <= 0) return null;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var found = FindExe(sub, exeName, depth - 1);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    private static void Log(string kind, string stage, string detail)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{kind}] {stage} {detail}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "app-launch.log"), line, Encoding.UTF8);
        }
        catch { }
    }
}
