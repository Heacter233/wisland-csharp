using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Plugins;
using Wisland.Services;

namespace Ff14RecruitPlugin;

/// <summary>
/// FF14 招募监控插件（NapCat 插件 napcat-plugin-ff14-recruit 的灵动岛移植版）。
///
/// 功能：
///   - 轮询 xivpf 招募板接口，按关键词轮换搜索；
///   - 与上次快照对比找出**新出现**的招募，去重后推送到灵动岛消息页；
///   - 支持屏蔽名单（玩家名 / 玩家@服务器）、屏蔽词、休息时段、推送数量上限；
///   - 插件页显示监控状态与最近命中；设置面板配置关键词与策略。
///
/// 数据：
///   静态表（随插件安装）：plugins/ff14-recruit/data/
///     category_map.json / worlds.json / ContentFinderCondition.csv / duty_override.csv
///   运行数据：data/plugins/ff14-recruit/
///     snapshot.json（每个关键词上次见到的招募 id）/ banlist.json / hits.json
///
/// 提示音：清单声明了 notify.sound。**默认不响** —— 需要用户在
/// 设置页 →「消息提示音」里打开总开关与「插件来源：FF14 招募监控」这一路。
/// </summary>
public sealed class Ff14RecruitPlugin : IWislandPlugin
{
    private const string PluginId = "ff14-recruit";
    private const string DefaultApiBase = "https://xivpf.littlenightmare.top/api/v2/listings";

    private IPluginContext? _ctx;
    private IDisposable? _timer;
    private readonly HttpClient _http = CreateHttp();
    private readonly object _lock = new();

    // ---------------- 静态数据 ----------------
    private readonly Dictionary<long, string> _categoryNames = new();
    private readonly Dictionary<string, long> _categoryByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, (string Name, string Dc, string DcNick)> _worlds = new();
    private readonly Dictionary<long, string> _defaultDuty = new();
    private readonly Dictionary<string, string> _overrideDuty = new(StringComparer.OrdinalIgnoreCase);

    // ---------------- 运行状态 ----------------
    private readonly Dictionary<string, DateTime> _pushed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Ban> _bans = new();
    private readonly Dictionary<string, List<string>> _snapshot = new();
    private readonly List<Hit> _hits = new();

    private int _keywordIndex;
    private bool _busy;
    private DateTime _nextCheckAt = DateTime.MinValue;
    private DateTime _lastCheckAt = DateTime.MinValue;
    private string _status = "尚未检查";
    private string _lastError = "";

    // ---------------- 页面控件 ----------------
    private TextBlock? _statusText;
    private TextBlock? _resultText;
    private TextBlock? _nextText;
    private Button? _toggleBtn;
    private StackPanel? _hitsPanel;
    private WrapPanel? _kwPanel;

    // ---------------- 搜索页 ----------------
    private readonly List<Hit> _searchResults = new();
    private TextBox? _searchBox;
    private StackPanel? _searchPanel;
    private TextBlock? _searchStatus;
    private bool _searching;

    private sealed class Listing
    {
        public string Id = "";
        public long CategoryId;
        public long DutyId;
        public long CreatedWorldId;
        public long HomeWorldId;
        public string Player = "";
        public string Description = "";
        public long MinItemLevel;
        public long SlotsFilled;
        public long SlotsAvailable;
        public long TimeLeftSeconds;
        public bool CrossWorld = true;
    }

    /// <summary>一条命中记录：页面上按卡片显示，字段足够渲染完整信息。</summary>
    private sealed class Hit
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Keyword { get; set; } = "";
        public string Category { get; set; } = "";
        public string Duty { get; set; } = "";
        /// <summary>发布服务器（大区昵称@服务器名）。</summary>
        public string Server { get; set; } = "";
        public string Player { get; set; } = "";
        public string HomeWorld { get; set; } = "";
        public long ItemLevel { get; set; }
        public long SlotsFilled { get; set; }
        public long SlotsAvailable { get; set; }
        public long TimeLeftSeconds { get; set; }
        public string Description { get; set; } = "";
        public bool CrossWorld { get; set; } = true;
    }

    private sealed class Ban
    {
        public string Target { get; set; } = "";
        public DateTime AddedAt { get; set; } = DateTime.Now;
        public DateTime? Expires { get; set; }
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        try
        {
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Wisland-FF14Recruit/1.0 (+https://github.com/Heacter233/wisland-csharp)");
        }
        catch { }
        return c;
    }

    // ---------------- 生命周期 ----------------

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;
        context.Log.Info($"FF14 招募监控已加载，宿主 {context.HostVersion}，数据目录 {context.DataDirectory}");

        // OnLoad 必须快：这里只读本地小文件（几张表 < 100KB），不做网络请求
        LoadStaticData(context.PluginDirectory);
        LoadSnapshot();
        LoadBans();
        LoadHits();

        // 每 5 秒醒一次，按"下次检查时间"决定是否真的去拉接口（间隔可配，无需重注册定时器）
        _timer = context.RegisterTimer(TimeSpan.FromSeconds(5), Tick);

        context.RegisterSettingsSection("FF14 招募监控", "关键词、检查间隔、休息时段与屏蔽词", BuildSettings);
        context.RegisterPage(new PluginPageDescriptor
        {
            Key = PluginId,
            Title = "FF14 招募监控",
            Glyph = "\uE8BD",
            Height = 470,
            Build = BuildPage,
        });

        // 第二个页面：自定义关键词搜索（结果同样是卡片列表）
        context.RegisterPage(new PluginPageDescriptor
        {
            Key = PluginId + "-search",
            Title = "招募搜索",
            Glyph = "\uE721",
            Height = 470,
            Build = BuildSearchPage,
        });

        var kws = Keywords();
        _status = kws.Count == 0
            ? "未配置关键词（去设置页填写）"
            : $"已加载 {kws.Count} 个关键词，监控{(Enabled ? "已开启" : "未开启")}";
    }

    public void OnUnload()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
        try { _http.Dispose(); } catch { }
        _ctx?.Log.Info("FF14 招募监控已卸载");
        _ctx = null;
    }

    // ---------------- 配置 ----------------

    private T Cfg<T>(string key, T fallback)
    {
        try
        {
            var ctx = _ctx;
            if (ctx == null) return fallback;
            var v = ctx.Config.Get(key, fallback);
            return v is null ? fallback : v;
        }
        catch { return fallback; }
    }

    private bool Enabled => Cfg("monitor_enabled", false);

    private int IntervalSec => Math.Clamp(Cfg("interval_sec", 90), 30, 1800);

    private int MaxPages => Math.Clamp(Cfg("max_pages", 2), 1, 5);

    private int MaxPushPerCheck => Math.Clamp(Cfg("max_push", 3), 1, 10);

    /// <summary>页面最多显示多少条命中记录（默认 10，可配置到 30）。</summary>
    private int MaxHits => Math.Clamp(Cfg("max_hits", 10), 5, 30);

    private string ApiBase
    {
        get
        {
            var v = Cfg("api_base", DefaultApiBase) ?? "";
            return string.IsNullOrWhiteSpace(v) ? DefaultApiBase : v.Trim();
        }
    }

    private List<string> Keywords() => SplitList(Cfg("keywords", ""));

    private List<string> BlockKeywords() => SplitList(Cfg("block_keywords", ""));

    private static List<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Replace('，', ',').Replace('；', ';')
                  .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    // ---------------- 静态数据 ----------------

    private void LoadStaticData(string pluginDir)
    {
        try
        {
            var dataDir = Path.Combine(pluginDir, "data");

            // 类别
            var catPath = Path.Combine(dataDir, "category_map.json");
            if (File.Exists(catPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(catPath));
                if (doc.RootElement.TryGetProperty("categories", out var cats))
                {
                    foreach (var c in cats.EnumerateArray())
                    {
                        var id = c.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
                        var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (id == 0 || name.Length == 0) continue;
                        _categoryNames[id] = name;
                        _categoryByAlias[name] = id;
                        if (c.TryGetProperty("aliases", out var alias) && alias.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in alias.EnumerateArray())
                            {
                                var s = a.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) _categoryByAlias[s!] = id;
                            }
                        }
                    }
                }
            }

            // 服务器
            var worldPath = Path.Combine(dataDir, "worlds.json");
            if (File.Exists(worldPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(worldPath));
                if (doc.RootElement.TryGetProperty("data_centers", out var dcs))
                {
                    foreach (var dc in dcs.EnumerateArray())
                    {
                        var dcName = dc.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                        var dcNick = dc.TryGetProperty("nickname", out var dk) ? dk.GetString() ?? dcName : dcName;
                        if (dc.TryGetProperty("worlds", out var ws))
                        {
                            foreach (var w in ws.EnumerateArray())
                            {
                                var id = w.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
                                var name = w.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                if (id == 0 || name.Length == 0) continue;
                                _worlds[id] = (name, dcName, dcNick);
                            }
                        }
                    }
                }
            }

            // 副本名（默认表）
            var cfcPath = Path.Combine(dataDir, "ContentFinderCondition.csv");
            if (File.Exists(cfcPath))
            {
                var started = false;
                foreach (var line in File.ReadAllLines(cfcPath, Encoding.UTF8))
                {
                    if (!started)
                    {
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        if (line.StartsWith("0,", StringComparison.Ordinal)) started = true;
                        else continue;
                    }
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;
                    if (!int.TryParse(parts[0].Trim(), out var dutyId)) continue;
                    // 名称里可能含逗号：取第 2 段到最后一段（最后一段是 NameShort）
                    var name = parts.Length > 3
                        ? string.Join(",", parts.Skip(1).Take(parts.Length - 2)).Trim()
                        : parts[1].Trim();
                    if (name.Length > 0) _defaultDuty[dutyId] = name;
                }
            }

            // 副本名（覆盖表）
            var ovPath = Path.Combine(dataDir, "duty_override.csv");
            if (File.Exists(ovPath))
            {
                var lines = File.ReadAllLines(ovPath, Encoding.UTF8);
                for (var i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var parts = lines[i].Split(',');
                    if (parts.Length < 3) continue;
                    if (!int.TryParse(parts[0].Trim(), out var catId)) continue;
                    if (!int.TryParse(parts[1].Trim(), out var dutyId)) continue;
                    _overrideDuty[$"{catId}_{dutyId}"] = parts[2].Trim();
                }
            }

            _ctx?.Log.Info($"静态表：类别 {_categoryNames.Count}、服务器 {_worlds.Count}、副本 {_defaultDuty.Count}、覆盖 {_overrideDuty.Count}");
        }
        catch (Exception ex)
        {
            _ctx?.Log.Error("加载静态数据失败", ex);
        }
    }

    private string CategoryName(long catId) =>
        _categoryNames.TryGetValue(catId, out var n) ? n : (catId == 0 ? "全部" : $"未知类型({catId})");

    private string DutyName(long catId, long dutyId)
    {
        if (dutyId == 0) return "不限";

        var key = $"{catId}_{dutyId}";
        if (_overrideDuty.TryGetValue(key, out var ov))
        {
            if (ov == "无数据") return "无数据";
            if (ov.Length > 0) return ov;
        }
        if (_defaultDuty.TryGetValue(dutyId, out var name) && name.Length > 0) return name;

        // 新版本内容可能还没进表（或表里就是空名）—— 用编号兜底，避免显示成"未知副本"
        return $"副本#{dutyId}";
    }

    private (string Name, string Dc, string DcNick) World(long id) =>
        _worlds.TryGetValue(id, out var w) ? w : ($"未知服务器({id})", "", "");

    // ---------------- 运行数据 ----------------

    private string DataFile(string name) =>
        Path.Combine(_ctx?.DataDirectory ?? AppContext.BaseDirectory, name);

    private void LoadSnapshot()
    {
        try
        {
            var p = DataFile("snapshot.json");
            if (!File.Exists(p)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            lock (_lock)
            {
                _snapshot.Clear();
                foreach (var kv in doc.RootElement.EnumerateObject())
                {
                    var list = new List<string>();
                    if (kv.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in kv.Value.EnumerateArray())
                        {
                            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                        }
                    }
                    _snapshot[kv.Name] = list;
                }
            }
        }
        catch (Exception ex) { _ctx?.Log.Warn("读取 snapshot.json 失败：" + ex.Message); }
    }

    private void SaveSnapshot()
    {
        try
        {
            Dictionary<string, List<string>> copy;
            lock (_lock) copy = _snapshot.ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(DataFile("snapshot.json"),
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex) { _ctx?.Log.Warn("写入 snapshot.json 失败：" + ex.Message); }
    }

    private void LoadBans()
    {
        try
        {
            var p = DataFile("banlist.json");
            lock (_lock)
            {
                _bans.Clear();
                if (!File.Exists(p)) return;
                var parsed = JsonSerializer.Deserialize<List<Ban>>(File.ReadAllText(p));
                if (parsed != null) _bans.AddRange(parsed);
            }
            CleanExpiredBans();
        }
        catch (Exception ex) { _ctx?.Log.Warn("读取 banlist.json 失败：" + ex.Message); }
    }

    private void SaveBans()
    {
        try
        {
            List<Ban> copy;
            lock (_lock) copy = _bans.ToList();
            File.WriteAllText(DataFile("banlist.json"),
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex) { _ctx?.Log.Warn("写入 banlist.json 失败：" + ex.Message); }
    }

    private void CleanExpiredBans()
    {
        lock (_lock)
            _bans.RemoveAll(b => b.Expires.HasValue && b.Expires.Value < DateTime.Now);
    }

    private bool IsBanned(Listing l)
    {
        var world = World(l.HomeWorldId).Name;
        var target1 = l.Player;
        var target2 = $"{l.Player}@{world}";
        lock (_lock)
        {
            return _bans.Any(b =>
                string.Equals(b.Target, target1, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(b.Target, target2, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void LoadHits()
    {
        try
        {
            var p = DataFile("hits.json");
            if (!File.Exists(p)) return;
            var parsed = JsonSerializer.Deserialize<List<Hit>>(File.ReadAllText(p));
            lock (_lock)
            {
                _hits.Clear();
                if (parsed != null) _hits.AddRange(parsed.Take(30));
            }
        }
        catch (Exception ex) { _ctx?.Log.Warn("读取 hits.json 失败：" + ex.Message); }
    }

    private void SaveHits()
    {
        try
        {
            List<Hit> copy;
            lock (_lock)
            {
                if (_hits.Count > 30) _hits.RemoveRange(30, _hits.Count - 30);
                copy = _hits.ToList();
            }
            File.WriteAllText(DataFile("hits.json"),
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex) { _ctx?.Log.Warn("写入 hits.json 失败：" + ex.Message); }
    }

    // ---------------- 监控主循环 ----------------

    private void Tick()
    {
        try
        {
            if (!Enabled || _busy) return;
            if (DateTime.Now < _nextCheckAt) return;
            _ = CheckAsync(manual: false);
        }
        catch (Exception ex) { _ctx?.Log.Error("监控定时器异常", ex); }
    }

    private bool InRestPeriod()
    {
        var startRaw = Cfg("rest_start", "02:00") ?? "";
        var endRaw = Cfg("rest_end", "06:30") ?? "";
        if (startRaw.Length == 0 || endRaw.Length == 0) return false;

        var start = ParseMinutes(startRaw);
        var end = ParseMinutes(endRaw);
        if (start == null || end == null) return false;

        var now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        return start <= end
            ? now >= start && now <= end
            : now >= start || now <= end;   // 跨天
    }

    private static int? ParseMinutes(string raw)
    {
        var parts = raw.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h < 0 || h > 23 || m < 0 || m > 59) return null;
        return h * 60 + m;
    }

    /// <summary>立即检查一次（页面按钮 / 定时器都走这里）。</summary>
    private async Task CheckAsync(bool manual)
    {
        if (_ctx == null) return;
        if (_busy) return;

        var kws = Keywords();
        if (kws.Count == 0)
        {
            SetStatus("未配置关键词（去设置页填写）");
            return;
        }

        if (!manual && InRestPeriod())
        {
            SetStatus($"休息时段（{Cfg("rest_start", "02:00")}-{Cfg("rest_end", "06:30")}），跳过");
            _nextCheckAt = DateTime.Now.AddSeconds(IntervalSec);
            return;
        }

        _busy = true;
        try
        {
            CleanExpiredBans();

            // 轮换关键词，避免每次都查同一个（接口对高频请求有限流）
            var kw = kws[_keywordIndex % kws.Count];
            _keywordIndex++;

            var fetched = await FetchAsync(kw, MaxPages, _ctx.ShutdownToken);

            // 只监控国服：worlds.json 里只有国服服务器，发布服务器不在表里的（国际服）一律丢弃。
            // 接口本身不区分区服，这一步在本地过滤，避免把国际服招募也推过来。
            var found = fetched.Where(l => _worlds.ContainsKey(l.CreatedWorldId)).ToList();
            var filteredOut = fetched.Count - found.Count;

            var ids = found.Select(l => l.Id).Where(id => id.Length > 0).Distinct().ToList();

            List<string> oldIds;
            lock (_lock)
            {
                oldIds = _snapshot.TryGetValue(kw, out var l) ? l.ToList() : new List<string>();
            }
            var oldSet = new HashSet<string>(oldIds, StringComparer.OrdinalIgnoreCase);
            var fresh = found.Where(l => !oldSet.Contains(l.Id)).ToList();

            var pushed = 0;
            var skippedBan = 0;
            var skippedBlock = 0;
            var skippedDup = 0;
            foreach (var l in fresh)
            {
                if (pushed >= MaxPushPerCheck) break;
                if (IsDuplicate(l.Id)) { skippedDup++; continue; }
                if (IsBanned(l)) { skippedBan++; continue; }
                if (HasBlockKeyword(l.Description)) { skippedBlock++; continue; }

                MarkPushed(l.Id);
                PushRecruit(kw, l);
                pushed++;
            }

            lock (_lock) _snapshot[kw] = ids;
            SaveSnapshot();

            _lastCheckAt = DateTime.Now;
            _lastError = "";
            SetStatus($"检查完成 · {kw} · 国服 {found.Count} 条"
                      + (filteredOut > 0 ? $"（过滤国际服 {filteredOut}）" : "")
                      + $" · 新增 {fresh.Count} · 推送 {pushed}"
                      + (skippedDup + skippedBan + skippedBlock > 0
                          ? $"（跳过 重复{skippedDup}/屏蔽{skippedBan}/屏蔽词{skippedBlock}）"
                          : ""));
        }
        catch (OperationCanceledException)
        {
            // 宿主要退出，静默
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _lastCheckAt = DateTime.Now;
            SetStatus("检查失败：" + ex.Message);
            _ctx?.Log.Warn("检查招募失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _nextCheckAt = DateTime.Now.AddSeconds(IntervalSec);
            UpdateUi();
        }
    }

    private bool IsDuplicate(string id)
    {
        lock (_lock)
        {
            if (!_pushed.TryGetValue(id, out var t)) return false;
            if ((DateTime.Now - t).TotalMinutes < 10) return true;
            _pushed.Remove(id);
            return false;
        }
    }

    private void MarkPushed(string id)
    {
        lock (_lock)
        {
            _pushed[id] = DateTime.Now;
            if (_pushed.Count > 800)
            {
                var cutoff = DateTime.Now.AddMinutes(-10);
                foreach (var k in _pushed.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _pushed.Remove(k);
            }
        }
    }

    private bool HasBlockKeyword(string description)
    {
        var blocks = BlockKeywords();
        if (blocks.Count == 0 || string.IsNullOrWhiteSpace(description)) return false;
        return blocks.Any(b => description.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<Listing>> FetchAsync(string keyword, int maxPages, CancellationToken ct)
    {
        var all = new List<Listing>();
        for (var page = 1; page <= maxPages; page++)
        {
            var batch = await FetchPageAsync(keyword, page, ct);
            all.AddRange(batch);
            if (batch.Count < 20) break;   // 接口每页 20 条，没满说明到最后一页
        }
        return all;
    }

    private async Task<List<Listing>> FetchPageAsync(string keyword, int page, CancellationToken ct)
    {
        var url = $"{ApiBase}?page={page}&per_page=20&search={Uri.EscapeDataString(keyword)}";

        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(20));
                using var resp = await _http.GetAsync(url, linked.Token);
                var text = await resp.Content.ReadAsStringAsync(linked.Token);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Head(text, 120)}");

                var list = new List<Listing>();
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in data.EnumerateArray()) list.Add(ParseListing(e));
                }
                return list;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == 1) await Task.Delay(1200, ct);
            }
        }
        throw new IOException($"请求招募接口失败：{last?.Message}");
    }

    private static string Head(string s, int n) =>
        s.Length <= n ? s.Replace("\r", " ").Replace("\n", " ") : s[..n].Replace("\r", " ").Replace("\n", " ");

    private static Listing ParseListing(JsonElement e)
    {
        var l = new Listing();
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id":
                    l.Id = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                    break;
                case "category_id": l.CategoryId = AsLong(p.Value); break;
                case "duty_id": l.DutyId = AsLong(p.Value); break;
                case "created_world_id": l.CreatedWorldId = AsLong(p.Value); break;
                case "home_world_id": l.HomeWorldId = AsLong(p.Value); break;
                case "player_name": l.Player = p.Value.GetString() ?? ""; break;
                case "description": l.Description = p.Value.GetString() ?? ""; break;
                case "min_item_level": l.MinItemLevel = AsLong(p.Value); break;
                case "slots_filled": l.SlotsFilled = AsLong(p.Value); break;
                case "slots_available": l.SlotsAvailable = AsLong(p.Value); break;
                case "time_left_seconds": l.TimeLeftSeconds = AsLong(p.Value); break;
                case "is_cross_world": l.CrossWorld = p.Value.ValueKind != JsonValueKind.False; break;
            }
        }
        return l;
    }

    private static long AsLong(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)) return v;
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s)) return s;
        return 0;
    }

    private static string FormatTimeLeft(long seconds)
    {
        var m = seconds / 60;
        var s = seconds % 60;
        return m > 0 ? $"{m}分{s}秒" : $"{s}秒";
    }

    /// <summary>把一条招募格式化成一行摘要（消息页正文）。</summary>
    private string FormatListing(Listing l)
    {
        var parts = new List<string>();
        var cat = CategoryName(l.CategoryId);
        var duty = DutyName(l.CategoryId, l.DutyId);
        parts.Add(duty == "不限" ? cat : $"{cat}：{duty}");

        var cw = World(l.CreatedWorldId);
        parts.Add(string.IsNullOrWhiteSpace(cw.DcNick) ? cw.Name : $"{cw.DcNick}@{cw.Name}");

        var desc = (l.Description ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        if (desc.Length > 0)
            parts.Add(desc.Length > 60 ? desc[..60] + "…" : desc);

        var hw = World(l.HomeWorldId);
        parts.Add($"{l.Player}@{hw.Name}");

        if (l.MinItemLevel > 0) parts.Add($"装等{l.MinItemLevel}");
        parts.Add($"位置{l.SlotsFilled}/{l.SlotsAvailable}");
        parts.Add($"剩余{FormatTimeLeft(l.TimeLeftSeconds)}");
        if (!l.CrossWorld) parts.Add("本地");

        return string.Join(" ｜ ", parts);
    }

    /// <summary>服务器显示名：大区昵称@服务器名（未知时退回服务器 id）。</summary>
    private string ServerDisplay(Listing l)
    {
        var w = World(l.CreatedWorldId);
        return string.IsNullOrWhiteSpace(w.DcNick) ? w.Name : $"{w.DcNick}@{w.Name}";
    }

    private void PushRecruit(string keyword, Listing l)
    {
        if (_ctx == null) return;

        // 消息页只放"服务器名 + 内容"：完整信息（类别/副本/玩家/装等/位置/剩余）留给插件页的卡片
        var server = ServerDisplay(l);
        var desc = (l.Description ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        if (desc.Length == 0) desc = $"{CategoryName(l.CategoryId)}：{DutyName(l.CategoryId, l.DutyId)}";
        var body = $"{server}：{desc}";

        try
        {
            _ctx.PushMessage(new PluginMessage
            {
                // Source 必须等于插件 id：宿主按它查 notify.sound 声明来决定能否播放提示音
                Source = PluginId,
                SourceName = "FF14 招募",
                Title = $"侦测到「{keyword}」· {server}",
                Body = body,
                Level = "info",
                IconGlyph = "\uE8BD",
                IconColor = "#C9A227",
            });
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("推送消息失败", ex);
            return;
        }

        var hw = World(l.HomeWorldId);
        lock (_lock)
        {
            _hits.Insert(0, new Hit
            {
                Time = DateTime.Now,
                Keyword = keyword,
                Category = CategoryName(l.CategoryId),
                Duty = DutyName(l.CategoryId, l.DutyId),
                Server = server,
                Player = l.Player,
                HomeWorld = hw.Name,
                ItemLevel = l.MinItemLevel,
                SlotsFilled = l.SlotsFilled,
                SlotsAvailable = l.SlotsAvailable,
                TimeLeftSeconds = l.TimeLeftSeconds,
                Description = desc,
                CrossWorld = l.CrossWorld,
            });
            if (_hits.Count > 30) _hits.RemoveRange(30, _hits.Count - 30);
        }
        SaveHits();
    }

    private void SetStatus(string text)
    {
        _status = text;
        try { _ctx?.Log.Debug(text); } catch { }
    }

    private void UpdateUi()
    {
        var ctx = _ctx;
        if (ctx == null) return;
        try
        {
            ctx.Ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    RefreshUi();
                    ctx.RequestRepaint();
                }
                catch { }
            }));
        }
        catch { }
    }

    // ---------------- 插件页面 ----------------

    private FrameworkElement BuildPage()
    {
        var root = new StackPanel { Margin = new Thickness(4, 4, 4, 0) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text("FF14 招募监控", "T1", 15, FontWeights.SemiBold));
        head.Children.Add(Text("  关键词轮换搜索 · 新招募推送到消息页", "T3", 11,
            margin: new Thickness(8, 2, 0, 0)));
        root.Children.Add(head);

        // 状态卡
        var statusCard = new Border { Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 10, 12, 10) };
        statusCard.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        var statusInner = new StackPanel();
        _statusText = Text("", "T2", 12.5);
        statusInner.Children.Add(_statusText);
        _resultText = Text("", "T3", 11, margin: new Thickness(0, 4, 0, 0));
        statusInner.Children.Add(_resultText);
        _nextText = Text("", "T3", 11, margin: new Thickness(0, 2, 0, 0));
        statusInner.Children.Add(_nextText);
        statusCard.Child = statusInner;
        root.Children.Add(statusCard);

        // 关键词
        var kwCard = new Border { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 10, 12, 10) };
        kwCard.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        var kwInner = new StackPanel();
        kwInner.Children.Add(Text("监控关键词", "T3", 11));
        _kwPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        kwInner.Children.Add(_kwPanel);
        kwCard.Child = kwInner;
        root.Children.Add(kwCard);

        // 按钮行
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _toggleBtn = SmallButton(Enabled ? "暂停监控" : "开启监控");
        _toggleBtn.Click += (_, _) =>
        {
            try
            {
                var on = !Enabled;
                _ctx?.Config.Set("monitor_enabled", on);
                _nextCheckAt = DateTime.MinValue;
                SetStatus(on ? "监控已开启" : "监控已暂停");
                RefreshUi();
                _ctx?.RequestRepaint();
            }
            catch (Exception ex) { _ctx?.Log.Error("切换监控失败", ex); }
        };
        row.Children.Add(_toggleBtn);

        var checkBtn = SmallButton("立即检查一次", 8);
        checkBtn.Click += (_, _) => { _ = CheckAsync(manual: true); };
        row.Children.Add(checkBtn);

        var msgBtn = SmallButton("打开消息页", 8);
        msgBtn.Click += (_, _) => _ctx?.RequestExpand("msg");
        row.Children.Add(msgBtn);
        root.Children.Add(row);

        // 最近命中
        root.Children.Add(Text("最近命中（卡片按时间倒序）", "T3", 11, margin: new Thickness(0, 12, 0, 0)));
        _hitsPanel = new StackPanel { Margin = new Thickness(0, 6, 4, 0) };
        // 全部命中装进一个容器，滚动查看（内容高度固定，避免把岛撑爆）
        var hitsScroll = new ScrollViewer
        {
            MaxHeight = 250,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };
        hitsScroll.SetResourceReference(FrameworkElement.StyleProperty, "AppScroll");
        hitsScroll.Content = _hitsPanel;
        root.Children.Add(hitsScroll);

        RefreshUi();
        return root;
    }

    private void RefreshUi()
    {
        if (_statusText != null)
        {
            _statusText.Text = _status;
            try
            {
                _statusText.SetResourceReference(TextBlock.ForegroundProperty,
                    Enabled ? "AppText" : "AppText3");
            }
            catch { }
        }

        if (_resultText != null)
        {
            _resultText.Text = _lastCheckAt == DateTime.MinValue
                ? "尚未检查"
                : $"最近检查：{_lastCheckAt:HH:mm:ss}" + (_lastError.Length > 0 ? $" · 失败：{_lastError}" : "");
        }

        if (_nextText != null)
        {
            _nextText.Text = !Enabled
                ? "监控未开启（设置页可开启）"
                : _nextCheckAt == DateTime.MinValue || _nextCheckAt <= DateTime.Now
                    ? "正在等待下一次检查…"
                    : $"下次检查：{_nextCheckAt:HH:mm:ss}（每 {IntervalSec} 秒）";
        }

        if (_toggleBtn != null)
        {
            _toggleBtn.Content = Enabled ? "暂停监控" : "开启监控";
        }

        if (_kwPanel != null)
        {
            _kwPanel.Children.Clear();
            var kws = Keywords();
            if (kws.Count == 0)
            {
                _kwPanel.Children.Add(Text("（未配置，去设置页填写）", "T3", 11));
            }
            else
            {
                foreach (var k in kws.Take(12))
                {
                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(8, 3, 8, 3),
                        Margin = new Thickness(0, 0, 4, 4),
                    };
                    chip.SetResourceReference(Border.BackgroundProperty, "AppPill");
                    var tb = Text(k, null, 11);
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "AppPillFg");
                    chip.Child = tb;
                    _kwPanel.Children.Add(chip);
                }
            }
        }

        if (_hitsPanel != null)
        {
            _hitsPanel.Children.Clear();
            List<Hit> hits;
            var limit = MaxHits;
            lock (_lock) hits = _hits.Take(limit).ToList();
            if (hits.Count == 0)
            {
                _hitsPanel.Children.Add(Text("还没有命中记录", "T3", 11));
            }
            else
            {
                foreach (var h in hits) _hitsPanel.Children.Add(BuildHitCard(h));
            }
        }
    }

    /// <summary>一条命中记录的卡片：完整信息（类别/副本、发布服务器、玩家、装等、位置、剩余、描述）。</summary>
    private FrameworkElement BuildHitCard(Hit h, bool showTime = true)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 8, 10, 8),
        };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");

        var box = new StackPanel();

        // 第一行：类别：副本 + 服务器 + 时间
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        // 第一行：类别：副本 · 服务器：xxx
        var title = Text($"{h.Category}：{h.Duty} · 服务器：{h.Server}", "T2", 12, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        if (showTime)
        {
            var time = Text(h.Time.ToString("HH:mm"), "T3", 10.5);
            time.FontFamily = (FontFamily)SafeResource("NumFont", new FontFamily("Segoe UI"));
            time.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(time, 1);
            head.Children.Add(time);
        }
        box.Children.Add(head);

        // 第二行：玩家 + 装等/位置/剩余
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(h.Player)) meta.Add($"玩家：{h.Player}@{h.HomeWorld}");
        if (h.ItemLevel > 0) meta.Add($"装等{h.ItemLevel}");
        meta.Add($"位置{h.SlotsFilled}/{h.SlotsAvailable}");
        meta.Add($"剩余{FormatTimeLeft(h.TimeLeftSeconds)}");
        if (!h.CrossWorld) meta.Add("本地");
        if (meta.Count > 0) box.Children.Add(Text(string.Join(" · ", meta), "T3", 10.5));

        // 第三行：描述（最多两行）
        if (!string.IsNullOrWhiteSpace(h.Description))
        {
            var desc = Text(h.Description, "T3", 11, margin: new Thickness(0, 4, 0, 0));
            desc.TextWrapping = TextWrapping.Wrap;
            desc.MaxHeight = 32;
            desc.TextTrimming = TextTrimming.CharacterEllipsis;
            desc.ToolTip = h.Description;
            box.Children.Add(desc);
        }

        card.Child = box;
        return card;
    }

    // ---------------- 搜索页 ----------------

    private FrameworkElement BuildSearchPage()
    {
        var root = new StackPanel { Margin = new Thickness(4, 4, 4, 0) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text("招募搜索", "T1", 15, FontWeights.SemiBold));
        head.Children.Add(Text("  输入关键词搜索当前招募板（仅国服，最多 30 条）", "T3", 11,
            margin: new Thickness(8, 2, 0, 0)));
        root.Children.Add(head);

        // 搜索行
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _searchBox = new TextBox
        {
            Width = 420,
            Height = 30,
            FontSize = 12,
            Text = Cfg("search_keyword", ""),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _searchBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) RunSearch();
        };
        row.Children.Add(_searchBox);

        var go = SmallButton("搜索", 8);
        go.Click += (_, _) => RunSearch();
        row.Children.Add(go);

        var clear = SmallButton("清空", 6);
        clear.Click += (_, _) =>
        {
            if (_searchBox != null) _searchBox.Text = "";
            _searchResults.Clear();
            RefreshSearchUi();
            if (_searchStatus != null) _searchStatus.Text = "输入关键词后回车或点「搜索」";
            _ctx?.RequestRepaint();
        };
        row.Children.Add(clear);
        root.Children.Add(row);

        _searchStatus = Text("输入关键词后回车或点「搜索」", "T3", 11, margin: new Thickness(0, 8, 0, 0));
        root.Children.Add(_searchStatus);

        _searchPanel = new StackPanel { Margin = new Thickness(0, 8, 4, 0) };
        var scroll = new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };
        scroll.SetResourceReference(FrameworkElement.StyleProperty, "AppScroll");
        scroll.Content = _searchPanel;
        root.Children.Add(scroll);

        RefreshSearchUi();
        return root;
    }

    /// <summary>执行一次自定义搜索（页面按钮 / 回车触发）。</summary>
    private void RunSearch()
    {
        if (_ctx == null || _searchBox == null || _searching) return;
        var kw = (_searchBox.Text ?? "").Trim();
        if (kw.Length == 0)
        {
            if (_searchStatus != null) _searchStatus.Text = "请输入搜索内容";
            return;
        }
        _ = RunSearchAsync(kw);
    }

    private async Task RunSearchAsync(string keyword)
    {
        if (_ctx == null) return;
        _searching = true;
        try
        {
            if (_searchStatus != null) _searchStatus.Text = $"正在搜索「{keyword}」…";
            _ctx.RequestRepaint();

            // 最多翻 2 页（40 条），过滤国际服后取前 30 条
            var fetched = await FetchAsync(keyword, 2, _ctx.ShutdownToken);
            var cn = fetched.Where(l => _worlds.ContainsKey(l.CreatedWorldId)).ToList();
            var taken = cn.Take(30).ToList();

            _searchResults.Clear();
            foreach (var l in taken) _searchResults.Add(ToListingHit(l));

            try { _ctx.Config.Set("search_keyword", keyword); } catch { }

            if (_searchStatus != null)
            {
                _searchStatus.Text = $"「{keyword}」· 国服 {cn.Count} 条"
                    + (fetched.Count > cn.Count ? $"（过滤国际服 {fetched.Count - cn.Count}）" : "")
                    + $" · 显示 {taken.Count} 条";
            }
            RefreshSearchUi();
            _ctx.RequestRepaint();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_searchStatus != null) _searchStatus.Text = "搜索失败：" + ex.Message;
            _ctx?.Log.Warn("搜索失败：" + ex.Message);
        }
        finally
        {
            _searching = false;
        }
    }

    private Hit ToListingHit(Listing l) => new Hit
    {
        Time = DateTime.Now,
        Keyword = "",
        Category = CategoryName(l.CategoryId),
        Duty = DutyName(l.CategoryId, l.DutyId),
        Server = ServerDisplay(l),
        Player = l.Player,
        HomeWorld = World(l.HomeWorldId).Name,
        ItemLevel = l.MinItemLevel,
        SlotsFilled = l.SlotsFilled,
        SlotsAvailable = l.SlotsAvailable,
        TimeLeftSeconds = l.TimeLeftSeconds,
        Description = (l.Description ?? "").Trim().Replace("\r", " ").Replace("\n", " "),
        CrossWorld = l.CrossWorld,
    };

    private void RefreshSearchUi()
    {
        if (_searchPanel == null) return;
        _searchPanel.Children.Clear();
        if (_searchResults.Count == 0)
        {
            _searchPanel.Children.Add(Text("暂无结果", "T3", 11));
            return;
        }
        foreach (var h in _searchResults) _searchPanel.Children.Add(BuildHitCard(h, showTime: false));
    }

    // ---------------- 设置面板 ----------------

    private FrameworkElement BuildSettings()
    {
        var root = new StackPanel();

        root.Children.Add(Text(
            "插件轮换搜索这些关键词，发现**新出现**的招募就推送到消息页。多个关键词用逗号分隔。"
            + "只保留国服（发布服务器在国服列表里）的招募，国际服会被过滤。",
            "T3", 11).Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        var kwBox = new TextBox
        {
            Text = Cfg("keywords", ""),
            Height = 64,
            Margin = new Thickness(0, 8, 0, 0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 12,
        };
        kwBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        root.Children.Add(kwBox);

        // 数字行
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var interval = NumBox("interval_sec", 90, 64);
        var pages = NumBox("max_pages", 2, 48);
        var maxPush = NumBox("max_push", 3, 48);
        var maxHits = NumBox("max_hits", 10, 48);
        row.Children.Add(Text("间隔(秒)", "T3", 11));
        row.Children.Add(interval);
        row.Children.Add(Text("最多页数", "T3", 11, margin: new Thickness(12, 0, 0, 0)));
        row.Children.Add(pages);
        row.Children.Add(Text("每次最多推送", "T3", 11, margin: new Thickness(12, 0, 0, 0)));
        row.Children.Add(maxPush);
        row.Children.Add(Text("最多显示", "T3", 11, margin: new Thickness(12, 0, 0, 0)));
        row.Children.Add(maxHits);
        root.Children.Add(row);

        // 休息时段
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row2.Children.Add(Text("休息时段", "T3", 11));
        var restStart = new TextBox
        {
            Text = Cfg("rest_start", "02:00"),
            Width = 64, Height = 30, Margin = new Thickness(8, 0, 4, 0), FontSize = 12,
        };
        restStart.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        row2.Children.Add(restStart);
        row2.Children.Add(Text("~", "T3", 11));
        var restEnd = new TextBox
        {
            Text = Cfg("rest_end", "06:30"),
            Width = 64, Height = 30, Margin = new Thickness(4, 0, 0, 0), FontSize = 12,
        };
        restEnd.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        row2.Children.Add(restEnd);
        row2.Children.Add(Text("（留空表示不设休息时段）", "T3", 10.5, margin: new Thickness(8, 0, 0, 0)));
        root.Children.Add(row2);

        // 屏蔽词
        root.Children.Add(Text("屏蔽词（描述里包含这些词的招募不推送，逗号分隔）", "T3", 11,
            margin: new Thickness(0, 10, 0, 0)));
        var block = new TextBox
        {
            Text = Cfg("block_keywords", ""),
            Height = 30, Margin = new Thickness(0, 6, 0, 0), FontSize = 12,
        };
        block.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        root.Children.Add(block);

        // 开关
        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var enable = new CheckBox { Content = "启用监控", IsChecked = Enabled, VerticalAlignment = VerticalAlignment.Center };
        enable.SetResourceReference(Control.ForegroundProperty, "AppText");
        enable.Click += (_, _) =>
        {
            try
            {
                _ctx?.Config.Set("monitor_enabled", enable.IsChecked == true);
                _nextCheckAt = DateTime.MinValue;
                RefreshUi();
                _ctx?.RequestRepaint();
            }
            catch (Exception ex) { _ctx?.Log.Error("切换监控失败", ex); }
        };
        toggleRow.Children.Add(enable);
        root.Children.Add(toggleRow);

        // 保存
        var save = new Button
        {
            Content = "保存设置",
            Height = 28,
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        save.Click += (_, _) =>
        {
            try
            {
                _ctx?.Config.Set("keywords", kwBox.Text ?? "");
                _ctx?.Config.Set("interval_sec", Math.Clamp(ParseInt(interval.Text, 90), 30, 1800));
                _ctx?.Config.Set("max_pages", Math.Clamp(ParseInt(pages.Text, 2), 1, 5));
                _ctx?.Config.Set("max_push", Math.Clamp(ParseInt(maxPush.Text, 3), 1, 10));
                _ctx?.Config.Set("max_hits", Math.Clamp(ParseInt(maxHits.Text, 10), 5, 30));
                _ctx?.Config.Set("rest_start", (restStart.Text ?? "").Trim());
                _ctx?.Config.Set("rest_end", (restEnd.Text ?? "").Trim());
                _ctx?.Config.Set("block_keywords", block.Text ?? "");
                _ctx?.Config.Set("monitor_enabled", enable.IsChecked == true);
                _nextCheckAt = DateTime.MinValue;
                SetStatus("设置已保存");
                RefreshUi();
                _ctx?.RequestRepaint();
                _ctx?.Log.Info("设置已保存");
            }
            catch (Exception ex) { _ctx?.Log.Error("保存设置失败", ex); }
        };
        root.Children.Add(save);

        // 重载静态表：改完 data/duty_override.csv 后不用重装插件
        var reloadBtn = new Button
        {
            Content = "重载数据表",
            Height = 28,
            Margin = new Thickness(8, 12, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        reloadBtn.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        reloadBtn.Click += (_, _) =>
        {
            try
            {
                var dir = _ctx?.PluginDirectory ?? "";
                _categoryNames.Clear(); _categoryByAlias.Clear();
                _worlds.Clear(); _defaultDuty.Clear(); _overrideDuty.Clear();
                LoadStaticData(dir);
                LoadBans();
                SetStatus($"数据表已重载：类别 {_categoryNames.Count}、服务器 {_worlds.Count}、副本 {_defaultDuty.Count}");
                RefreshUi();
                _ctx?.RequestRepaint();
            }
            catch (Exception ex) { _ctx?.Log.Error("重载数据表失败", ex); }
        };

        // 注意顺序：必须先把 save 从 root 摘掉，再加到新的行容器里，
        // 否则会抛"指定的元素已经是另一个元素的逻辑子元素"
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Remove(save);
        saveRow.Children.Add(save);
        saveRow.Children.Add(reloadBtn);
        root.Children.Add(saveRow);

        root.Children.Add(Text(
            "提示：推送的消息默认静音。要让它响，去 设置页 →「消息提示音」打开总开关，"
            + "并打开「插件来源：FF14 招募监控」这一路。",
            "T3", 10.5, margin: new Thickness(0, 10, 0, 0))
            .Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        root.Children.Add(Text(
            "数据：静态表在 plugins/ff14-recruit/data/；快照与屏蔽名单在 data/plugins/ff14-recruit/。",
            "T3", 10.5, margin: new Thickness(0, 6, 0, 0))
            .Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        return root;
    }

    private TextBox NumBox(string key, int fallback, double width)
    {
        var box = new TextBox
        {
            Text = Cfg(key, fallback).ToString(),
            Width = width,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
        };
        box.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        return box;
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse((text ?? "").Trim(), out var v) ? v : fallback;

    // ---------------- UI 小工具 ----------------

    private Button SmallButton(string label, double leftMargin = 0)
    {
        var b = new Button
        {
            Content = label,
            Height = 26,
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        return b;
    }

    private static TextBlock Text(string text, string? style, double size,
        FontWeight? weight = null, Thickness margin = default)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
        };
        if (weight.HasValue) tb.FontWeight = weight.Value;
        if (style != null)
        {
            try { tb.SetResourceReference(FrameworkElement.StyleProperty, style); } catch { }
        }
        else
        {
            try { tb.SetResourceReference(TextBlock.ForegroundProperty, "AppText"); } catch { }
        }
        return tb;
    }

    private static object SafeResource(string key, object fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is { } v) return v;
        }
        catch { }
        return fallback;
    }
}

/// <summary>小扩展：链式设置属性，让构建代码更短。</summary>
internal static class FluentExt
{
    public static T Also<T>(this T self, Action<T> action)
    {
        try { action(self); } catch { }
        return self;
    }
}
