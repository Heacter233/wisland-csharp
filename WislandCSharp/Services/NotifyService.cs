using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Wisland.Services;

/// <summary>一条消息记录。</summary>
public sealed class NotifyItem
{
    public string Kind { get; init; } = "other";
    public string App { get; init; } = "";
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Sender { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;

    public string Description =>
        Body.Length > 0 ? Body
        : Title.Length > 0 ? Title
        : Kind == "mail" ? "新邮件"
        : Sender.Length > 0 ? $"{Sender} 发来消息"
        : "新消息";
}

/// <summary>
/// 消息服务：真实读取 Windows 通知数据库
/// (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db)。
///
/// 实现方式：WNS 会独占该库，因此先把 db/wal/shm 复制到数据目录再以只读方式打开。
/// 通知负载有两种：
///   · &lt;toast&gt; XML —— 解析 &lt;text&gt; 作为标题/正文；
///   · &lt;badge value="N"/&gt; —— 作为该应用的未读数。
/// 未读数优先使用 badge，没有 badge 时按"最近仍未清除的 toast 条数"估算。
/// </summary>
public sealed class NotifyService : IDisposable
{
    private static readonly string SourceDb = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    // 应用标识 → 内部 kind
    private static readonly (string Pattern, string Kind, string Name)[] Rules =
    {
        ("QQ", "qq", "QQ"),
        ("Tencent", "qq", "QQ"),
        ("WeChat", "wechat", "微信"),
        ("Weixin", "wechat", "微信"),
        ("WeChatAppEx", "wechat", "微信"),
        ("communicationsapps", "mail", "邮件"),
        ("Outlook", "mail", "邮件"),
        ("Mail", "mail", "邮件"),
        ("Skype", "other", "Skype"),
        ("Discord", "other", "Discord"),
        ("Telegram", "other", "Telegram"),
    };

    private readonly IConfigService _config;
    private readonly List<NotifyItem> _history = new();
    private readonly Dictionary<string, int> _counts = new();
    private readonly Dictionary<string, DateTime> _clearedAt = new();
    private readonly HashSet<string> _seen = new();
    private readonly object _lock = new();
    private readonly string _snapshot;
    private Timer? _timer;
    private volatile bool _busy;
    private bool _primed;

    /// <summary>通知库（含 -wal）最后一次写入时间：用来判断"这一轮到底有没有新东西"。</summary>
    private long _lastStamp;

    /// <summary>已处理到的最大 ArrivalTime（FILETIME），增量查询用。</summary>
    private long _lastArrival;

    /// <summary>最近一次读到的 badge 未读数（按来源）。</summary>
    private readonly Dictionary<string, int> _badges = new();

    /// <summary>增量查询的重叠窗口（FILETIME 单位 = 100ns）：覆盖 WNS 写入与读取的时序错位。</summary>
    private const long ArrivalOverlap = 10 * 1000 * 1000;   // 1 秒

    public event EventHandler<Dictionary<string, int>>? CountsChanged;
    public event EventHandler<NotifyItem>? Notified;

    public IReadOnlyList<NotifyItem> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public IReadOnlyDictionary<string, int> Counts
    {
        get { lock (_lock) return new Dictionary<string, int>(_counts); }
    }

    public NotifyService(IConfigService config)
    {
        _config = config;
        _snapshot = Path.Combine(AppPaths.DataDir, "notify-snapshot.db");
    }

    /// <summary>开始轮询通知库（间隔默认 4 秒）。</summary>
    public void Start()
    {
        if (_timer != null) return;
        var ms = Math.Clamp(_config.Get("notify.poll_ms", 4000), 1000, 60000);
        _timer = new Timer(_ => Poll(), null, 800, ms);
    }

    private void Poll()
    {
        if (_busy) return;
        _busy = true;
        try { PollCore(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotifyService] {ex.Message}");
            Debug.WriteLine($"[NotifyService] poll: {ex}");
        }
        finally { _busy = false; }
    }

    private void PollCore()
    {
        if (!File.Exists(SourceDb)) return;

        // 先看通知库有没有被写过：没变就整轮跳过。
        // 早期每 4 秒无条件整库复制 wpndatabase.db（+wal +shm，可达数 MB）并重解码 200 条 toast，
        // 空闲时也持续吃 CPU 与 SSD 写入量；现在绝大多数轮次只做一次 stat。
        var stamp = SourceStamp();
        if (_primed && stamp == _lastStamp) return;
        if (!CopySnapshot()) return;      // 正被 WNS 写着，下轮再试
        _lastStamp = stamp;

        var found = new List<NotifyItem>();
        var badges = new Dictionary<string, int>();
        var maxArrival = _lastArrival;

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _snapshot,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // ArrivalTime 为 FILETIME；增量读取（带 1 秒重叠去重），上限 200 条兜底
            cmd.CommandText = @"
                SELECT h.PrimaryId, n.Type, n.Tag, n.ArrivalTime, n.Payload
                FROM Notification n
                JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                WHERE n.ArrivalTime > @since
                ORDER BY n.ArrivalTime DESC
                LIMIT 200";
            cmd.Parameters.AddWithValue("@since", _lastArrival > 0 ? Math.Max(0, _lastArrival - ArrivalOverlap) : 0);

            using var reader = cmd.ExecuteReader();
            var cutoff = DateTime.Now.AddDays(-3);
            while (reader.Read())
            {
                var primaryId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var arrival = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                if (arrival > maxArrival) maxArrival = arrival;

                var time = FromFileTime(arrival);
                if (time < cutoff) continue;

                var payload = reader.IsDBNull(4) ? null : reader.GetValue(4) as byte[];
                if (payload is not { Length: > 0 }) continue;
                var xml = DecodePayload(payload);
                if (xml.Length == 0) continue;

                var (kind, appName) = Classify(primaryId);
                if (kind == "other") continue;   // 游戏/系统通知不进消息页

                // badge 通知：直接给未读数
                var badge = ParseBadge(xml);
                if (badge >= 0)
                {
                    badges[kind] = Math.Max(badges.GetValueOrDefault(kind), badge);
                    continue;
                }

                var (title, body) = ParseToast(xml);
                if (title.Length == 0 && body.Length == 0) continue;

                found.Add(new NotifyItem
                {
                    Kind = kind,
                    App = appName,
                    Name = kind switch { "qq" => "QQ", "wechat" => "微信", "mail" => "邮件", _ => appName },
                    Title = title,
                    Sender = title,
                    Body = body,
                    Time = time,
                });
            }
        }

        _lastArrival = maxArrival;

        // 新条目去重（重叠窗口会重复读到上一轮的最后一条）
        List<NotifyItem> fresh = new();
        lock (_lock)
        {
            foreach (var item in found)
            {
                var key = $"{item.Time.Ticks}|{item.Kind}|{item.Body}";
                if (!_seen.Add(key)) continue;                 // 已处理过
                fresh.Add(item);
                _history.Add(item);
            }
            // 合并后按时间倒序，只留 60 条
            _history.Sort((a, b) => b.Time.CompareTo(a.Time));
            if (_history.Count > 60) _history.RemoveRange(60, _history.Count - 60);
            // _seen 只增不减会长期占内存：清空前先保留当前历史里的键
            if (_seen.Count > 1000)
            {
                _seen.Clear();
                foreach (var h in _history) _seen.Add($"{h.Time.Ticks}|{h.Kind}|{h.Body}");
            }
        }

        // 统计未读：badge 优先，否则统计清除时间之后的通知
        var newCounts = new Dictionary<string, int>();
        List<NotifyItem> historySnapshot;
        DateTime clearedQq, clearedWechat, clearedMail;
        lock (_lock)
        {
            foreach (var kv in badges) _badges[kv.Key] = Math.Max(_badges.GetValueOrDefault(kv.Key), kv.Value);
            historySnapshot = _history.ToList();
            clearedQq = _clearedAt.GetValueOrDefault("qq", DateTime.MinValue);
            clearedWechat = _clearedAt.GetValueOrDefault("wechat", DateTime.MinValue);
            clearedMail = _clearedAt.GetValueOrDefault("mail", DateTime.MinValue);
        }

        foreach (var kind in new[] { "qq", "wechat", "mail" })
        {
            if (_badges.TryGetValue(kind, out var b)) { newCounts[kind] = b; continue; }
            var cleared = kind switch
            {
                "qq" => clearedQq,
                "wechat" => clearedWechat,
                _ => clearedMail,
            };
            newCounts[kind] = historySnapshot.Count(f => f.Kind == kind && f.Time > cleared);
        }

        Dictionary<string, int>? countsSnapshot = null;
        lock (_lock)
        {
            var changed = !DictEqual(_counts, newCounts);
            foreach (var kv in newCounts) _counts[kv.Key] = kv.Value;
            // 注意：事件必须在锁外触发。UI 线程收到事件后可能回读 Counts（需要这把锁），
            // 在锁内同步 Invoke 会立刻死锁。
            if (changed) countsSnapshot = new Dictionary<string, int>(_counts);
            if (!_primed) { _primed = true; fresh.Clear(); }   // 首轮只建立基线，不弹提示
        }

        if (countsSnapshot != null) CountsChanged?.Invoke(this, countsSnapshot);

        foreach (var item in fresh)
        {
            MirrorToInbox(item);
            Notified?.Invoke(this, item);
        }
    }

    /// <summary>通知库（含 -wal）的最后写入时间 + 大小，用于判断是否需要重新读取。</summary>
    private long SourceStamp()
    {
        try
        {
            long stamp = 0;
            foreach (var path in new[] { SourceDb, SourceDb + "-wal" })
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) continue;
                    stamp = unchecked(stamp * 31 + fi.LastWriteTimeUtc.Ticks);
                    stamp = unchecked(stamp * 31 + fi.Length);
                }
                catch { }
            }
            return stamp;
        }
        catch { return 0; }
    }

    private bool CopySnapshot()
    {
        try
        {
            File.Copy(SourceDb, _snapshot, overwrite: true);
            // -wal / -shm 由 WNS 持续写入，复制常常失败；缺失时退回主库内容即可
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                var src = SourceDb + ext;
                try
                {
                    if (File.Exists(src)) File.Copy(src, _snapshot + ext, overwrite: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return true;
        }
        catch (IOException) { return false; }        // 正被 WNS 写入，下一轮再试
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception ex) { Debug.WriteLine($"[NotifyService] copy: {ex.Message}"); return false; }
    }

    private static DateTime FromFileTime(long fileTime)
    {
        try { return fileTime > 0 ? DateTime.FromFileTime(fileTime) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static string DecodePayload(byte[] payload)
    {
        try
        {
            var text = Encoding.UTF8.GetString(payload).Trim('\0', ' ', '\r', '\n');
            return text.StartsWith("<", StringComparison.Ordinal) ? text : "";
        }
        catch { return ""; }
    }

    private static int ParseBadge(string xml)
    {
        if (!xml.Contains("<badge", StringComparison.OrdinalIgnoreCase)) return -1;
        var m = Regex.Match(xml, "value\\s*=\\s*\"(\\d+)\"");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>从 toast XML 里取标题与正文（&lt;text&gt; 按出现顺序）。</summary>
    private static (string Title, string Body) ParseToast(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants()
                .Where(e => e.Name.LocalName == "text")
                .Select(e => (e.Value ?? "").Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (texts.Count == 0) return ("", "");
            if (texts.Count == 1) return (texts[0], "");
            return (texts[0], string.Join(" ", texts.Skip(1)));
        }
        catch { return ("", ""); }
    }

    private static (string Kind, string App) Classify(string primaryId)
    {
        foreach (var (pattern, kind, name) in Rules)
        {
            if (primaryId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (kind, name);
        }
        // 取 "Microsoft.Foo_xxx!App" 里的显示名
        var app = primaryId;
        var bang = app.IndexOf('!');
        if (bang > 0) app = app[..bang];
        var dot = app.LastIndexOf('.');
        if (dot > 0 && dot < app.Length - 1) app = app[(dot + 1)..];
        var us = app.IndexOf('_');
        if (us > 0) app = app[..us];
        return ("other", app.Length > 0 ? app : "通知");
    }

    private static bool DictEqual(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in b) if (a.GetValueOrDefault(kv.Key) != kv.Value) return false;
        return true;
    }

    /// <summary>系统通知来源的显示信息（名字 / 图标字形 / 颜色），用于镜像到 Inbox。</summary>
    public static (string Name, string Glyph, string Color) KindInfo(string kind) => kind switch
    {
        "qq" => ("QQ", "\uE8F2", "#12B7F5"),
        "wechat" => ("微信", "\uE8BD", "#07C160"),
        "mail" => ("邮件", "\uE715", "#FF9F0A"),
        _ => ("通知", "\uE8BD", "#5A9BF0"),
    };

    /// <summary>把系统通知镜像进 Inbox（config: island.notify_to_inbox，默认开）。</summary>
    private void MirrorToInbox(NotifyItem item)
    {
        try
        {
            if (!_config.Get("island.notify_to_inbox", true)) return;
            var (name, glyph, color) = KindInfo(item.Kind);
            ServiceHub.Inbox.PushTrusted(new PluginMessage
            {
                Source = item.Kind,
                SourceName = string.IsNullOrWhiteSpace(item.Name) ? name : item.Name,
                Title = string.IsNullOrWhiteSpace(item.Title) ? name : item.Title,
                Body = string.IsNullOrWhiteSpace(item.Body) ? item.Description : item.Body,
                Level = "info",
                IconGlyph = glyph,
                IconColor = color,
                Time = item.Time,
                Kind = item.Kind,
            });
        }
        catch { }
    }

    /// <summary>外部（例如手动推送/测试）记录一条消息。</summary>
    public void Emit(NotifyItem item)
    {
        lock (_lock)
        {
            _history.Insert(0, item);
            if (_history.Count > 60) _history.RemoveAt(_history.Count - 1);
            _counts[item.Kind] = _counts.GetValueOrDefault(item.Kind) + 1;
        }
        MirrorToInbox(item);
        Notified?.Invoke(this, item);
        CountsChanged?.Invoke(this, new Dictionary<string, int>(Counts));
    }

    /// <summary>清除某类未读（只清本地计数，不动 Windows 通知中心）。</summary>
    public void Clear(string kind)
    {
        // _clearedAt 必须在同一把锁里读写：
        // 之前轮询线程（Timer）锁外读、UI 线程锁外写，普通 Dictionary 并发读写
        // 轻则抛 InvalidOperationException（被 Poll 的 catch 吞掉丢数据），重则内部桶损坏。
        lock (_lock)
        {
            _clearedAt[kind] = DateTime.Now;
            _counts[kind] = 0;
        }
        CountsChanged?.Invoke(this, new Dictionary<string, int>(Counts));
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            foreach (var kind in new[] { "qq", "wechat", "mail" }) _clearedAt[kind] = now;
            foreach (var k in _counts.Keys.ToList()) _counts[k] = 0;
        }
        CountsChanged?.Invoke(this, new Dictionary<string, int>(Counts));
    }

    /// <summary>打开对应程序（config.json → notify.open.qq / wechat / mail）。</summary>
    public void Open(string kind)
    {
        var configured = _config.Get($"notify.open.{kind}", "");
        // 配置路径 → 运行中进程 → 已知安装路径 → 开始菜单快捷方式 → 卸载注册表 → URI 协议
        // （只靠 tencent:// / weixin:// 在多数机器上没注册，点了会毫无反应）
        // 后台执行：这条链最长会轮询 8.5 秒，放 UI 线程上会把整个岛冻住
        _ = AppLauncher.LaunchAsync(kind, configured);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        try { if (File.Exists(_snapshot)) File.Delete(_snapshot); } catch { }
    }
}