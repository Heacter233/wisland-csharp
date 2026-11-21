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
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Plugins;
using Wisland.Services;

namespace Ff14PricePlugin;

/// <summary>
/// FF14 物品价格查询插件（移植自 缪丝的挖宝会计 / ff14_price_tool.py）。
///
/// 数据源：
///   - 物品名 → 物品ID：随插件打包的离线词典 data/items.tsv（由 ffxiv-datamining-cn 的 Item.csv 生成），
///     词典未命中时尝试 cafemaker / xivapi 在线兜底（原程序的灰机 wiki 方案已跳过）；
///   - 价格：Universalis v2 市场布告板接口，按大区分批查询（陆行鸟/莫古力/猫小胖/豆豆柴）。
///
/// 展示：
///   - 汇总表：每个服务器一行（最低价 / 数量 / 挂单数 / 总库存 / 更新时间），按最低价升序；
///   - 明细表：每条挂单一行（单价 / 服务器 / 数量 / 品质 / 雇员 / 更新时间），按单价升序；
///   - 两张表都用 DataGrid（行/列虚拟化，几百上千行也不卡）。
///
/// 筛选：大区（全/鸟/猪/猫/狗）、品质（全/HQ/NQ）、只看有货、价格区间、最少数量。
/// 常用词：常用物品（★收藏）+ 最近 5 条搜索历史，点一下即查。
/// </summary>
public sealed class Ff14PricePlugin : IWislandPlugin
{
    private const string PluginId = "ff14-price";
    private const int DetailRowLimit = 2000;   // 明细表最多绑定的行数（再多也没人会看）

    private static readonly Dictionary<string, string> DcMap = new()
    {
        ["鸟"] = "陆行鸟",
        ["猪"] = "莫古力",
        ["猫"] = "猫小胖",
        ["狗"] = "豆豆柴",
    };

    private static readonly string[] DcKeys = { "全", "鸟", "猪", "猫", "狗" };
    private static readonly string[] QualityKeys = { "全", "HQ", "NQ" };

    private IPluginContext? _ctx;
    private readonly HttpClient _http = CreateHttp();
    private readonly object _lock = new();

    // ---------------- 物品词典 ----------------
    private sealed class ItemEntry
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        /// <summary>m=可上市场板，h=可HQ。</summary>
        public string Flags { get; init; } = "";
        public bool Marketable => Flags.Contains('m');
        public bool CanHq => Flags.Contains('h');
    }

    private readonly List<ItemEntry> _items = new();
    private Dictionary<string, ItemEntry> _byExact = new(StringComparer.Ordinal);   // 规范化全名
    private Dictionary<string, ItemEntry> _byCompact = new(StringComparer.Ordinal); // 去空白标点

    // ---------------- 价格数据 ----------------
    private sealed class ListingRow
    {
        public long Price { get; init; }
        public string World { get; init; } = "";
        public string Dc { get; init; } = "";
        public long Quantity { get; init; }
        public bool Hq { get; init; }
        public string Retainer { get; init; } = "";
        public DateTime Updated { get; init; }

        public string HqText => Hq ? "HQ" : "";
        public string UpdatedText => Updated == DateTime.MinValue ? "" : Updated.ToString("MM-dd HH:mm");
    }

    private sealed class SummaryRow
    {
        public string World { get; init; } = "";
        public string Dc { get; init; } = "";
        public long MinPrice { get; init; }
        public long MinQuantity { get; init; }
        public long Listings { get; init; }
        public long TotalQuantity { get; init; }
        public DateTime Updated { get; init; }

        public string HqText { get; init; } = "";
        public string UpdatedText => Updated == DateTime.MinValue ? "" : Updated.ToString("MM-dd HH:mm");
    }

    private sealed class DcCacheEntry
    {
        public DateTime FetchedAt { get; init; }
        public List<ListingRow> Rows { get; init; } = new();
    }

    private readonly Dictionary<string, DcCacheEntry> _cache = new(StringComparer.Ordinal);

    // ---------------- 界面控件 ----------------
    private TextBox? _searchBox;
    private TextBlock? _statusText;
    private TextBlock? _countText;
    private WrapPanel? _chipsPanel;
    private Grid? _gridHost;
    private DataGrid? _summaryGrid;
    private DataGrid? _detailGrid;
    private Button? _summaryBtn;
    private Button? _detailBtn;
    private Button? _favBtn;
    private StackPanel? _dcSegRow;
    private StackPanel? _qualitySegRow;

    private long _lastItemId;
    private string _lastItemName = "";
    private int _queryToken;
    private bool _busy;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try { c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Wisland-FF14Price/1.0"); } catch { }
        return c;
    }

    // ---------------- 生命周期 ----------------

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;
        LoadDictionary(Path.Combine(context.PluginDirectory, "data", "items.tsv"));

        context.RegisterPage(new PluginPageDescriptor
        {
            Key = PluginId,
            Title = "物品价格",
            Glyph = "\uE8C7",
            Height = 500,
            Build = BuildPage,
        });

        context.RegisterSettingsSection("FF14 物品价格", "默认筛选、缓存与词典信息", BuildSettings);

        context.Log.Info($"FF14 物品价格已加载：词典 {_items.Count} 条（可上板 {_items.Count(x => x.Marketable)} 条）");
    }

    public void OnUnload()
    {
        try { _http.Dispose(); } catch { }
        _ctx = null;
    }

    // ---------------- 词典 ----------------

    private void LoadDictionary(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _ctx?.Log.Warn("物品词典缺失：" + path);
                return;
            }
            var count = 0;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[0], out var id)) continue;
                var name = parts[1].Trim();
                if (name.Length == 0) continue;
                var flags = parts.Length > 2 ? parts[2].Trim() : "";
                var entry = new ItemEntry { Id = id, Name = name, Flags = flags };
                _items.Add(entry);
                count++;
            }

            _byExact = new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
            _byCompact = new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
            foreach (var e in _items)
            {
                var norm = Normalize(e.Name);
                _byExact[norm] = e;
                var compact = Compact(norm);
                if (compact.Length > 0) _byCompact[compact] = e;
            }
            _ctx?.Log.Info($"物品词典加载完成：{count} 条");
        }
        catch (Exception ex)
        {
            _ctx?.Log.Error("加载物品词典失败", ex);
        }
    }

    /// <summary>规范化：全角转半角 + 小写 + 去首尾空白（与 Python 版一致）。</summary>
    private static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            var code = (int)ch;
            sb.Append(code >= 0xFF01 && code <= 0xFF5E ? (char)(code - 0xFEE0) : ch);
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>压缩：再去掉空白与常见标点，方便"秘银 矿"/"秘银矿"都能命中。</summary>
    private static string Compact(string norm)
    {
        if (string.IsNullOrEmpty(norm)) return "";
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '·' or '・' or '.' or ',' or '，' or '、' or '-' or '_' or '\'' or '"' or '（' or '）' or '(' or ')' or '：' or ':' or '【' or '】' or '[' or ']') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>物品名 → 物品ID。优先离线词典，未命中再试在线接口（原程序的在线方案）。</summary>
    private async Task<(int Id, string Name, string Note)> ResolveItemAsync(string raw, CancellationToken ct)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return (0, "", "");

        // 纯数字 → 直接当物品 ID
        if (int.TryParse(trimmed, out var directId) && directId > 0)
        {
            var known = _items.FirstOrDefault(x => x.Id == directId);
            return (directId, known?.Name ?? $"物品ID {directId}", "直接使用物品ID");
        }

        var norm = Normalize(trimmed);
        if (_byExact.TryGetValue(norm, out var exact))
            return (exact.Id, exact.Name, "词典精确匹配");

        var compact = Compact(norm);
        if (compact.Length > 0 && _byCompact.TryGetValue(compact, out var compactHit))
            return (compactHit.Id, compactHit.Name, "词典忽略空格/标点匹配");

        // 模糊：前缀优先，其次包含；多个候选取名字最短的（通常就是用户想要的）
        var prefixHits = _items.Where(x => x.Marketable && Compact(Normalize(x.Name)).StartsWith(compact, StringComparison.Ordinal))
                               .OrderBy(x => x.Name.Length).Take(8).ToList();
        if (prefixHits.Count > 0)
        {
            var pick = prefixHits[0];
            var note = prefixHits.Count > 1
                ? $"前缀匹配 {prefixHits.Count} 个（如 {string.Join(" / ", prefixHits.Take(3).Select(x => x.Name))}），已选「{pick.Name}」"
                : "词典前缀匹配";
            return (pick.Id, pick.Name, note);
        }

        var containsHits = _items.Where(x => x.Marketable && Compact(Normalize(x.Name)).Contains(compact, StringComparison.Ordinal))
                                 .OrderBy(x => x.Name.Length).Take(8).ToList();
        if (containsHits.Count > 0)
        {
            var pick = containsHits[0];
            var note = containsHits.Count > 1
                ? $"包含匹配 {containsHits.Count} 个（如 {string.Join(" / ", containsHits.Take(3).Select(x => x.Name))}），已选「{pick.Name}」"
                : "词典包含匹配";
            return (pick.Id, pick.Name, note);
        }

        // 在线兜底（cafemaker → xivapi）；这些接口在本机经常不可用，失败就如实报错
        var online = await ResolveOnlineAsync(trimmed, ct);
        if (online.Id > 0) return online;

        throw new InvalidOperationException(
            $"词典里找不到「{trimmed}」，在线解析也失败（cafemaker / xivapi 不可达）。可以直接输入物品ID再查。");
    }

    private async Task<(int Id, string Name, string Note)> ResolveOnlineAsync(string keyword, CancellationToken ct)
    {
        foreach (var lang in new[] { "chs", "en" })
        {
            try
            {
                var url = "https://cafemaker.wakingsands.com/search?indexes=Item&string="
                          + Uri.EscapeDataString(keyword) + "&language=" + lang + "&columns=ID,Name,Name_en";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("Results", out var results) || results.GetArrayLength() == 0) continue;
                var first = results[0];
                if (!first.TryGetProperty("ID", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                var name = first.TryGetProperty("Name", out var n) ? n.GetString() ?? keyword : keyword;
                return (id, name, $"在线词典命中（cafemaker/{lang}）");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _ctx?.Log.Debug($"cafemaker({lang}) 失败：{ex.Message}"); }
        }

        try
        {
            var url = "https://xivapi.com/search?indexes=Item&string=" + Uri.EscapeDataString(keyword) + "&language=chs";
            using var resp = await _http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("Results", out var results) && results.GetArrayLength() > 0)
                {
                    var first = results[0];
                    if (first.TryGetProperty("ID", out var idEl) && idEl.TryGetInt32(out var id))
                    {
                        var name = first.TryGetProperty("Name", out var n) ? n.GetString() ?? keyword : keyword;
                        return (id, name, "在线词典命中（xivapi）");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _ctx?.Log.Debug("xivapi 失败：" + ex.Message); }

        return (0, "", "");
    }

    // ---------------- 价格查询 ----------------

    private async Task<List<ListingRow>> FetchAsync(int itemId, string dcKey, bool forceRefresh, CancellationToken ct)
    {
        var dcNames = dcKey == "全" ? DcMap.Values.ToList() : new List<string> { DcMap[dcKey] };
        var cacheSeconds = Math.Clamp(Cfg("cache_seconds", 60), 0, 3600);
        var now = DateTime.Now;

        var tasks = dcNames.Select(async dcName =>
        {
            lock (_lock)
            {
                if (!forceRefresh && cacheSeconds > 0 && _cache.TryGetValue(dcName + "#" + itemId, out var ce)
                    && ce.FetchedAt.AddSeconds(cacheSeconds) > now)
                {
                    return ce.Rows;
                }
            }

            var rows = await FetchOneDcAsync(dcName, itemId, ct);
            lock (_lock) _cache[dcName + "#" + itemId] = new DcCacheEntry { FetchedAt = DateTime.Now, Rows = rows };
            return rows;
        });

        var parts = await Task.WhenAll(tasks);
        var all = new List<ListingRow>();
        foreach (var p in parts) all.AddRange(p);
        return all;
    }

    private async Task<List<ListingRow>> FetchOneDcAsync(string dcName, int itemId, CancellationToken ct)
    {
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(dcName)}/{itemId}?listings=300&entries=0";
        var rows = new List<ListingRow>();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _ctx?.Log.Warn($"{dcName} 价格请求失败：HTTP {(int)resp.StatusCode}");
                return rows;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
                return rows;

            foreach (var e in listings.EnumerateArray())
            {
                var world = Str(e, "worldName");
                if (world.Length == 0) continue;
                rows.Add(new ListingRow
                {
                    Price = Long(e, "pricePerUnit"),
                    World = world,
                    Dc = dcName,
                    Quantity = Long(e, "quantity"),
                    Hq = e.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True,
                    Retainer = Str(e, "retainerName"),
                    Updated = UnixToLocal(Long(e, "lastReviewTime")),
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _ctx?.Log.Warn($"{dcName} 价格请求异常：{ex.Message}");
        }
        return rows;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static DateTime UnixToLocal(long seconds)
    {
        if (seconds <= 0) return DateTime.MinValue;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime; }
        catch { return DateTime.MinValue; }
    }

    // ---------------- 查询入口 ----------------

    private void RunQuery(bool forceRefresh)
    {
        if (_ctx == null || _searchBox == null || _busy) return;
        var keyword = (_searchBox.Text ?? "").Trim();
        if (keyword.Length == 0)
        {
            SetStatus("请输入物品名或物品ID");
            return;
        }
        _ = QueryAsync(keyword, forceRefresh);
    }

    private async Task QueryAsync(string keyword, bool forceRefresh)
    {
        if (_ctx == null) return;
        var token = ++_queryToken;
        _busy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            SetStatus($"正在解析「{keyword}」…");
            var (itemId, itemName, note) = await ResolveItemAsync(keyword, _ctx.ShutdownToken);
            if (itemId <= 0) { SetStatus("未找到物品"); return; }

            _lastItemId = itemId;
            _lastItemName = itemName;
            SaveSearchHistory(itemName);

            SetStatus($"正在查询「{itemName}」的市场价…");
            var listings = await FetchAsync(itemId, Cfg("dc", "全"), forceRefresh, _ctx.ShutdownToken);
            if (token != _queryToken) return;   // 期间又发起了新查询

            var filtered = ApplyFilters(listings);
            var summary = BuildSummary(filtered);
            var details = filtered.Take(DetailRowLimit).ToList();

            sw.Stop();

            var allWorlds = filtered.Select(x => x.World).Distinct().Count();
            var minPrice = filtered.Count > 0 ? filtered[0].Price : 0;
            var minWorld = filtered.Count > 0 ? filtered[0].World : "";
            SetStatus($"{itemName} (ID {itemId}) · {note}"
                      + (filtered.Count > 0
                          ? $" · 最低 {minPrice:N0} @{minWorld} · 挂单 {filtered.Count}（{allWorlds} 个服务器）"
                          : " · 没有符合条件的挂单")
                      + $" · 用时 {sw.ElapsedMilliseconds}ms");

            if (_countText != null)
                _countText.Text = $"汇总 {summary.Count} 行 · 明细 {details.Count} 行" + (filtered.Count > DetailRowLimit ? $"（明细仅显示前 {DetailRowLimit} 条）" : "");

            if (_summaryGrid != null) _summaryGrid.ItemsSource = summary;
            if (_detailGrid != null) _detailGrid.ItemsSource = details;
            if (_favBtn != null) _favBtn.Content = IsFavorite(itemName) ? "★ 已收藏" : "★ 收藏";

            RefreshChips();
            UpdateViewMode();
            _ctx.RequestRepaint();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("查询失败：" + ex.Message);
            _ctx?.Log.Warn("价格查询失败：" + ex.Message);
        }
        finally
        {
            if (token == _queryToken) _busy = false;
        }
    }

    /// <summary>套用界面上的筛选条件（品质 / 只看有货 / 价格区间 / 最少数量）。</summary>
    private List<ListingRow> ApplyFilters(List<ListingRow> raw)
    {
        var quality = Cfg("quality", "全");
        var onlyInStock = Cfg("only_in_stock", true);
        var minPrice = Cfg("min_price", 0);
        var maxPrice = Cfg("max_price", 0);
        var minQty = Cfg("min_quantity", 0);

        _ctx?.Log.Debug($"筛选条件：quality={quality} 只看有货={onlyInStock} 价格={minPrice}~{maxPrice} 数量≥{minQty} 原始={raw.Count}");

        IEnumerable<ListingRow> q = raw;
        if (quality == "HQ") q = q.Where(x => x.Hq);
        else if (quality == "NQ") q = q.Where(x => !x.Hq);
        if (onlyInStock) q = q.Where(x => x.Quantity > 0);
        if (minPrice > 0) q = q.Where(x => x.Price >= minPrice);
        if (maxPrice > 0) q = q.Where(x => x.Price <= maxPrice);
        if (minQty > 0) q = q.Where(x => x.Quantity >= minQty);

        return q.OrderBy(x => x.Price).ThenBy(x => x.World).ToList();
    }

    /// <summary>按服务器聚合：最低价、对应数量/品质、挂单数、总库存、最后上传时间。</summary>
    private static List<SummaryRow> BuildSummary(List<ListingRow> filtered) =>
        filtered.GroupBy(x => (x.World, x.Dc))
                .Select(g =>
                {
                    var first = g.OrderBy(x => x.Price).First();
                    return new SummaryRow
                    {
                        World = g.Key.World,
                        Dc = g.Key.Dc,
                        MinPrice = first.Price,
                        MinQuantity = first.Quantity,
                        HqText = first.Hq ? "HQ" : "",
                        Listings = g.Count(),
                        TotalQuantity = g.Sum(x => x.Quantity),
                        Updated = g.Max(x => x.Updated),
                    };
                })
                .OrderBy(x => x.MinPrice).ThenBy(x => x.World)
                .ToList();

    // ---------------- 配置读写 ----------------

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

    private void SetCfg(string key, object value)
    {
        try { _ctx?.Config.Set(key, value); } catch { }
    }

    // ---------------- 常用物品 / 历史 ----------------

    private List<string> SplitList(string raw) =>
        (raw ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

    private List<string> Favorites() => SplitList(Cfg("favorites", ""));

    private List<string> History() => SplitList(Cfg("history", ""));

    private bool IsFavorite(string name) =>
        Favorites().Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private void ToggleFavorite()
    {
        var name = _lastItemName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var fav = Favorites();
        if (fav.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
            fav.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        else
            fav.Insert(0, name);
        SetCfg("favorites", string.Join("\n", fav.Take(12)));
        if (_favBtn != null) _favBtn.Content = IsFavorite(name) ? "★ 已收藏" : "★ 收藏";
        RefreshChips();
        _ctx?.RequestRepaint();
    }

    private void SaveSearchHistory(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var hist = History();
        hist.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        hist.Insert(0, name);
        SetCfg("history", string.Join("\n", hist.Take(5)));
    }

    // ---------------- 页面 ----------------

    private FrameworkElement BuildPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 搜索行
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 常用词
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 筛选
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 视图切换 + 统计
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 表格（固定高度，见 _gridHost）
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态

        // 0 标题
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 0) };
        head.Children.Add(Text("物品价格", "T1", 15, FontWeights.SemiBold));
        head.Children.Add(Text("  市场布告板最低价 · 数据源 Universalis", "T3", 11, margin: new Thickness(8, 2, 0, 0)));
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // 1 搜索行
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 8, 4, 0) };
        _searchBox = new TextBox
        {
            Width = 300, Height = 30, FontSize = 12,
            Text = Cfg("last_item", ""),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _searchBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) RunQuery(forceRefresh: false); };
        searchRow.Children.Add(_searchBox);

        var query = SmallButton("查询", 8);
        query.Click += (_, _) => RunQuery(forceRefresh: false);
        searchRow.Children.Add(query);

        var refresh = SmallButton("刷新", 6);
        refresh.Click += (_, _) => RunQuery(forceRefresh: true);
        searchRow.Children.Add(refresh);

        _favBtn = SmallButton(IsFavorite(Cfg("last_item", "") ?? "") ? "★ 已收藏" : "★ 收藏", 6);
        _favBtn.Click += (_, _) => ToggleFavorite();
        searchRow.Children.Add(_favBtn);
        Grid.SetRow(searchRow, 1);
        root.Children.Add(searchRow);

        // 2 常用词
        _chipsPanel = new WrapPanel { Margin = new Thickness(4, 6, 4, 0) };
        Grid.SetRow(_chipsPanel, 2);
        root.Children.Add(_chipsPanel);

        // 3 筛选行
        var filterRow = new WrapPanel { Margin = new Thickness(4, 6, 4, 0) };
        _dcSegRow = Segmented("dc", DcKeys, Cfg("dc", "全"), v => { SetCfg("dc", v); RunQuery(forceRefresh: true); });
        _qualitySegRow = Segmented("quality", QualityKeys, Cfg("quality", "全"), v => { SetCfg("quality", v); RunQuery(forceRefresh: false); });
        filterRow.Children.Add(Label("大区"));
        filterRow.Children.Add(_dcSegRow);
        filterRow.Children.Add(Label("品质", 12));
        filterRow.Children.Add(_qualitySegRow);

        var inStock = new CheckBox
        {
            Content = "只看有货", IsChecked = Cfg("only_in_stock", true),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0),
        };
        inStock.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AppText3");
        inStock.Click += (_, _) => { SetCfg("only_in_stock", inStock.IsChecked == true); RunQuery(forceRefresh: false); };
        filterRow.Children.Add(inStock);

        filterRow.Children.Add(Label("价格", 10));
        filterRow.Children.Add(NumberBox("min_price", 0, 70, "最低"));
        filterRow.Children.Add(Label("-", 4));
        filterRow.Children.Add(NumberBox("max_price", 0, 70, "最高"));
        filterRow.Children.Add(Label("数量≥", 10));
        filterRow.Children.Add(NumberBox("min_quantity", 0, 60, "0"));
        Grid.SetRow(filterRow, 3);
        root.Children.Add(filterRow);

        // 4 视图切换 + 行数统计
        var viewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 8, 4, 0) };
        _summaryBtn = SmallButton("汇总（按服务器）");
        _summaryBtn.Click += (_, _) => { SetCfg("view_mode", "summary"); UpdateViewMode(); _ctx?.RequestRepaint(); };
        _detailBtn = SmallButton("明细（每条挂单）", 6);
        _detailBtn.Click += (_, _) => { SetCfg("view_mode", "detail"); UpdateViewMode(); _ctx?.RequestRepaint(); };
        viewRow.Children.Add(_summaryBtn);
        viewRow.Children.Add(_detailBtn);
        _countText = Text("", "T3", 10.5, margin: new Thickness(12, 0, 0, 0));
        viewRow.Children.Add(_countText);
        Grid.SetRow(viewRow, 4);
        root.Children.Add(viewRow);

        // 5 表格
        _gridHost = new Grid { Margin = new Thickness(4, 6, 4, 0), Height = 232 };
        _summaryGrid = BuildSummaryGrid();
        _detailGrid = BuildDetailGrid();
        _gridHost.Children.Add(_summaryGrid);
        _gridHost.Children.Add(_detailGrid);
        Grid.SetRow(_gridHost, 5);
        root.Children.Add(_gridHost);

        // 6 状态
        _statusText = Text("输入物品名（如「陈旧的瞪羚革地图」）或物品ID，回车查询", "T3", 10.5,
            margin: new Thickness(4, 6, 4, 2));
        _statusText.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_statusText, 6);
        root.Children.Add(_statusText);

        RefreshChips();
        UpdateViewMode();
        return root;
    }

    private static TextBlock Label(string text, double left = 0) => new TextBlock()
    {
        Text = text,
        FontSize = 10.5,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 4, 0),
    }.Also(tb => { try { tb.SetResourceReference(TextBlock.ForegroundProperty, "AppText3"); } catch { } });

    private TextBox NumberBox(string key, long fallback, double width, string tooltip)
    {
        var box = new TextBox
        {
            Text = Cfg(key, fallback).ToString(),
            Width = width, Height = 26, FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = tooltip,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        void Commit()
        {
            var v = long.TryParse((box.Text ?? "").Trim(), out var n) ? Math.Max(0, n) : fallback;
            SetCfg(key, v);
            RunQuery(forceRefresh: false);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    /// <summary>紧凑分段控件（大区/品质用），选中用主题胶囊样式。</summary>
    private StackPanel Segmented(string key, string[] values, string current, Action<string> onChanged)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        foreach (var v in values)
        {
            var btn = new Button
            {
                Content = v,
                Height = 24,
                MinWidth = 30,
                Margin = new Thickness(0, 0, 2, 0),
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 10.5,
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,
            };
            var selected = v == current;
            ApplySegStyle(btn, selected);
            btn.Click += (_, _) =>
            {
                foreach (var child in sp.Children.OfType<Button>())
                    ApplySegStyle(child, ReferenceEquals(child, btn));
                onChanged(v);
            };
            sp.Children.Add(btn);
        }
        return sp;
    }

    private static void ApplySegStyle(Button b, bool selected)
    {
        try
        {
            if (selected)
            {
                b.SetResourceReference(Button.BackgroundProperty, "AppPill");
                b.SetResourceReference(Button.ForegroundProperty, "AppPillFg");
                b.BorderThickness = new Thickness(0);
                b.SetResourceReference(Button.StyleProperty, "SegBtn");
            }
            else
            {
                b.Background = Brushes.Transparent;
                b.SetResourceReference(Button.ForegroundProperty, "AppText3");
                b.BorderThickness = new Thickness(0);
                b.SetResourceReference(Button.StyleProperty, "SegBtn");
            }
        }
        catch
        {
            b.Background = Brushes.Transparent;
        }
    }

    private DataGrid BuildSummaryGrid()
    {
        var g = NewGrid();
        g.Columns.Add(Col("服务器", "World", 122));
        g.Columns.Add(Col("大区", "Dc", 70));
        g.Columns.Add(Col("最低价", "MinPrice", 84, right: true, format: "N0", bold: true));
        g.Columns.Add(Col("数量", "MinQuantity", 58, right: true, format: "N0"));
        g.Columns.Add(Col("品质", "HqText", 46));
        g.Columns.Add(Col("挂单数", "Listings", 64, right: true, format: "N0"));
        g.Columns.Add(Col("总库存", "TotalQuantity", 72, right: true, format: "N0"));
        g.Columns.Add(Col("更新时间", "UpdatedText", 94));
        return g;
    }

    private DataGrid BuildDetailGrid()
    {
        var g = NewGrid();
        g.Columns.Add(Col("单价", "Price", 84, right: true, format: "N0", bold: true));
        g.Columns.Add(Col("服务器", "World", 116));
        g.Columns.Add(Col("大区", "Dc", 70));
        g.Columns.Add(Col("数量", "Quantity", 58, right: true, format: "N0"));
        g.Columns.Add(Col("品质", "HqText", 46));
        g.Columns.Add(Col("雇员", "Retainer", 118));
        g.Columns.Add(Col("更新时间", "UpdatedText", 94));
        return g;
    }

    /// <summary>主题化的 DataGrid：透明背景、无网格线、行/列虚拟化、可点表头排序。</summary>
    private DataGrid NewGrid()
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            RowHeaderWidth = 0,
            RowHeight = 24,
            ColumnHeaderHeight = 26,
            FontSize = 11,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
        try
        {
            g.SetResourceReference(Control.ForegroundProperty, "AppText");
            g.RowBackground = Brushes.Transparent;
            g.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "AppCard");
        }
        catch { }

        // 单元格/表头样式（代码里建 Style，颜色全部走 DynamicResource，跟着主题变）
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AppText")));
        cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        g.CellStyle = cellStyle;

        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AppText3")));
        headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, 10.5));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        headerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        g.ColumnHeaderStyle = headerStyle;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AppText")));
        g.RowStyle = rowStyle;

        return g;
    }

    private DataGridColumn Col(string header, string path, double width,
        bool right = false, string? format = null, bool bold = false)
    {
        var binding = new Binding(path) { Mode = BindingMode.OneWay };
        if (format != null) binding.StringFormat = "{0:" + format + "}";
        var col = new DataGridTextColumn
        {
            Header = header,
            Binding = binding,
            Width = new DataGridLength(width),
            IsReadOnly = true,
            CanUserSort = true,
        };
        if (right || bold)
        {
            var style = new Style(typeof(TextBlock));
            if (right) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            if (bold) style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            col.ElementStyle = style;
        }
        return col;
    }

    private void UpdateViewMode()
    {
        var mode = Cfg("view_mode", "summary");
        if (_summaryGrid != null) _summaryGrid.Visibility = mode == "detail" ? Visibility.Collapsed : Visibility.Visible;
        if (_detailGrid != null) _detailGrid.Visibility = mode == "detail" ? Visibility.Visible : Visibility.Collapsed;
        if (_summaryBtn != null) _summaryBtn.Opacity = mode == "detail" ? 0.55 : 1.0;
        if (_detailBtn != null) _detailBtn.Opacity = mode == "detail" ? 1.0 : 0.55;

        // 只切 Visibility 的话，宿主那边可能还没标记该区域重绘 —— 强制走一次布局并把区域标脏
        try
        {
            _gridHost?.UpdateLayout();
            _gridHost?.InvalidateVisual();
            _summaryGrid?.InvalidateVisual();
            _detailGrid?.InvalidateVisual();
        }
        catch { }
    }

    private void RefreshChips()
    {
        if (_chipsPanel == null) return;
        _chipsPanel.Children.Clear();

        var fav = Favorites();
        var hist = History().Where(h => !fav.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();

        foreach (var f in fav)
            _chipsPanel.Children.Add(Chip("★ " + f, true));
        foreach (var h in hist)
            _chipsPanel.Children.Add(Chip(h, false));

        if (fav.Count == 0 && hist.Count == 0)
            _chipsPanel.Children.Add(Text("常用/历史为空：查到物品后点「★ 收藏」就会出现在这里", "T3", 10.5));
    }

    private FrameworkElement Chip(string label, bool favorite)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.Hand,
        };
        border.SetResourceReference(Border.BackgroundProperty, favorite ? "AppPill" : "AppCard");
        var tb = Text(label, null, 10.5);
        tb.SetResourceReference(TextBlock.ForegroundProperty, favorite ? "AppPillFg" : "AppText3");
        border.Child = tb;

        var name = label.StartsWith("★ ") ? label[2..] : label;
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (_searchBox != null) _searchBox.Text = name;
            RunQuery(forceRefresh: false);
        };
        return border;
    }

    private void SetStatus(string text)
    {
        if (_statusText != null) _statusText.Text = text;
        try { _ctx?.Log.Debug(text); } catch { }
    }

    private Button SmallButton(string label, double leftMargin = 0)
    {
        var b = new Button
        {
            Content = label,
            Height = 26,
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11,
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

    // ---------------- 设置面板 ----------------

    private FrameworkElement BuildSettings()
    {
        var root = new StackPanel();
        root.Children.Add(Text("默认筛选（插件页里也有同样的开关，改这里等于改默认值）", "T3", 11)
            .Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        // 默认大区
        root.Children.Add(Text("默认大区", "T3", 10.5, margin: new Thickness(0, 10, 0, 4)));
        var dcRow = new WrapPanel();
        foreach (var k in DcKeys)
        {
            var b = SmallButton(k, 4);
            b.Click += (_, _) => { SetCfg("dc", k); };
            dcRow.Children.Add(b);
        }
        root.Children.Add(dcRow);

        root.Children.Add(Text("默认品质", "T3", 10.5, margin: new Thickness(0, 10, 0, 4)));
        var qRow = new WrapPanel();
        foreach (var k in QualityKeys)
        {
            var b = SmallButton(k, 4);
            b.Click += (_, _) => { SetCfg("quality", k); };
            qRow.Children.Add(b);
        }
        root.Children.Add(qRow);

        root.Children.Add(Text("缓存时间（秒）", "T3", 10.5, margin: new Thickness(0, 12, 0, 4)));
        var cacheBox = new TextBox
        {
            Text = Cfg("cache_seconds", 60).ToString(),
            Width = 80, Height = 26, FontSize = 11,
        };
        cacheBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        root.Children.Add(cacheBox);

        var save = new Button
        {
            Content = "保存设置",
            Height = 28,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        save.Click += (_, _) =>
        {
            try
            {
                SetCfg("cache_seconds", Math.Clamp(int.TryParse((cacheBox.Text ?? "").Trim(), out var c) ? c : 60, 0, 3600));
                _ctx?.Log.Info("价格插件设置已保存");
            }
            catch (Exception ex) { _ctx?.Log.Error("保存价格插件设置失败", ex); }
        };
        root.Children.Add(save);

        root.Children.Add(Text(
            $"物品词典：{_items.Count} 条（可上市场板 {_items.Count(x => x.Marketable)} 条，可HQ {_items.Count(x => x.CanHq)} 条）\n"
            + "词典来自 ffxiv-datamining-cn 的 Item.csv，随插件安装；未命中时会尝试 cafemaker / xivapi 在线兜底。\n"
            + "价格来自 Universalis 公开接口，仅供参考；大量查询时注意缓存时间别设太小。",
            "T3", 10.5, margin: new Thickness(0, 12, 0, 0))
            .Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        return root;
    }
}

/// <summary>链式设置属性的小工具。</summary>
internal static class FluentExt
{
    public static T Also<T>(this T self, Action<T> action)
    {
        try { action(self); } catch { }
        return self;
    }
}
