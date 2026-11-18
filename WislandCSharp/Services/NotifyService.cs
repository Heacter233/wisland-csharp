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
        if (!CopySnapshot()) return;

        var found = new List<NotifyItem>();
        var badges = new Dictionary<string, int>();

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
            // ArrivalTime 为 FILETIME；只取最近 3 天，避免历史噪声
            cmd.CommandText = @"
                SELECT h.PrimaryId, n.Type, n.Tag, n.ArrivalTime, n.Payload
                FROM Notification n
                JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                ORDER BY n.ArrivalTime DESC
                LIMIT 200";

            using var reader = cmd.ExecuteReader();
            var cutoff = DateTime.Now.AddDays(-3);
            while (reader.Read())
            {
                var primaryId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var arrival = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
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

                var key = $"{primaryId}|{arrival}|{title}|{body}";
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

                lock (_lock)
                {
                    if (_seen.Add(key)) { /* 新条目 */ }
                }
            }
        }

        // 统计未读：badge 优先，否则统计清除时间之后的 toast
        var newCounts = new Dictionary<string, int>();
        foreach (var kind in new[] { "qq", "wechat", "mail" })
        {
            if (badges.TryGetValue(kind, out var b)) { newCounts[kind] = b; continue; }
            var cleared = _clearedAt.GetValueOrDefault(kind, DateTime.MinValue);
            newCounts[kind] = found.Count(f => f.Kind == kind && f.Time > cleared);
        }

        List<NotifyItem> fresh = new();
        Dictionary<string, int>? countsSnapshot = null;
        lock (_lock)
        {
            var before = new HashSet<string>(_history.Select(h => $"{h.Time.Ticks}|{h.Kind}|{h.Body}"));
            _history.Clear();
            _history.AddRange(found.Take(60));
            fresh = _primed
                ? found.Where(f => !before.Contains($"{f.Time.Ticks}|{f.Kind}|{f.Body}")).Take(5).ToList()
                : new List<NotifyItem>();
            _primed = true;

            var changed = !DictEqual(_counts, newCounts);
            foreach (var kv in newCounts) _counts[kv.Key] = kv.Value;
            // 注意：事件必须在锁外触发。UI 线程收到事件后可能回读 Counts（需要这把锁），
            // 在锁内同步 Invoke 会立刻死锁。
            if (changed) countsSnapshot = new Dictionary<string, int>(_counts);
        }

        if (countsSnapshot != null) CountsChanged?.Invoke(this, countsSnapshot);

        foreach (var item in fresh)
        {
            MirrorToInbox(item);
            Notified?.Invoke(this, item);
        }
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
            ServiceHub.Inbox.Push(new PluginMessage
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
        _clearedAt[kind] = DateTime.Now;
        lock (_lock) _counts[kind] = 0;
        CountsChanged?.Invoke(this, new Dictionary<string, int>(Counts));
    }

    public void ClearAll()
    {
        foreach (var kind in new[] { "qq", "wechat", "mail" }) _clearedAt[kind] = DateTime.Now;
        lock (_lock)
            foreach (var k in _counts.Keys.ToList()) _counts[k] = 0;
        CountsChanged?.Invoke(this, new Dictionary<string, int>(Counts));
    }

    /// <summary>打开对应程序（config.json → notify.open.qq / wechat / mail）。</summary>
    public void Open(string kind)
    {
        var path = _config.Get($"notify.open.{kind}", "");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            catch { }
        }
        try
        {
            var uri = kind switch
            {
                "mail" => "mailto:",
                "qq" => "tencent://",
                "wechat" => "weixin://",
                _ => "",
            };
            if (uri.Length > 0) Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine($"[NotifyService] Open: {ex.Message}"); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        try { if (File.Exists(_snapshot)) File.Delete(_snapshot); } catch { }
    }
}