using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Plugins;
using Wisland.Services;

namespace StockPlugin;

/// <summary>
/// 股票行情插件：自选股（增删）+ 大盘指数 + 分时/日K 迷你走势图 + 涨跌提醒。
///
/// 数据源（均为东方财富公开接口，JSON/UTF-8，无需 key）：
///   列表  https://push2.eastmoney.com/api/qt/ulist.np/get
///   分时  https://push2his.eastmoney.com/api/qt/stock/trends2/get
///   日K   https://push2his.eastmoney.com/api/qt/stock/kline/get
///
/// 代码写法：sh600000 / sz000001 / 600000（自动判断交易所）
/// </summary>
public sealed class StockPlugin : IWislandPlugin
{
    /// <summary>列表接口（多域名故障切换：主站被限流时换备用域）。</summary>
    private const string ListPath = "/api/qt/ulist.np/get";
    private const string TrendPath = "/api/qt/stock/trends2/get";
    private const string KlinePath = "/api/qt/stock/kline/get";

    /// <summary>行情接口备用域名（东方财富对单域名有频率限制）。</summary>
    private static readonly string[] QuoteHosts =
    {
        "https://push2.eastmoney.com",
        "https://push2delay.eastmoney.com",
        "https://82.push2.eastmoney.com",
    };

    /// <summary>历史接口（分时/日K）备用域名。</summary>
    private static readonly string[] HistoryHosts =
    {
        "https://push2his.eastmoney.com",
        "https://push2.eastmoney.com",
    };

    private const int MaxCodes = 12;
    private static readonly string[] IndexIds = { "1.000001", "0.399001", "0.399006" };

    private IPluginContext? _ctx;
    private IDisposable? _timer;
    private readonly HttpClient _http = CreateHttp();

    private readonly List<Quote> _quotes = new();
    private readonly List<Quote> _indices = new();
    private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);

    private string _status = "尚未刷新";
    private bool _busy;
    private string _selected = "";
    private string _chartMode = "trend";
    private DateTime _lastSuccess = DateTime.MinValue;
    private DateTime _lastFailAt = DateTime.MinValue;

    // 页面控件
    private StackPanel? _rows;
    private StackPanel? _indexRow;
    private TextBlock? _statusText;
    private TextBlock? _chartTitle;
    private MiniChart? _chart;
    private Button? _trendBtn;
    private Button? _klineBtn;
    private Border? _addHost;
    private TextBox? _addBox;

    // ---------------- 生命周期 ----------------

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;
        _chartMode = context.Config.Get("chart_mode", "trend");
        context.RegisterSettingsSection("股票行情", "自选股、刷新间隔与涨跌提醒", BuildSettings);
        context.RegisterPage(new PluginPageDescriptor
        {
            Key = "stock",
            Title = "股票行情",
            Glyph = "\uE9D2",
            Height = 340,
            Build = BuildPage,
        });

        LoadCache();

        var interval = Math.Max(15, context.Config.Get("refresh_sec", 60));
        _timer = context.RegisterTimer(TimeSpan.FromSeconds(interval), () => _ = RefreshAsync());
        _ = RefreshAsync();
    }

    public void OnUnload()
    {
        _timer?.Dispose();
        _timer = null;
        // 释放连接池（示例被复制到各处，泄漏会累积）
        try { _http.Dispose(); } catch { }
        _ctx = null;
    }

    // ---------------- 配置 ----------------

    private List<string> GetCodes()
    {
        var raw = _ctx?.Config.Get("codes", "sh600000,sz000001,sz300750") ?? "";
        return raw.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(Normalize)
                  .Where(c => c.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .Take(MaxCodes)
                  .ToList();
    }

    private void SaveCodes(IEnumerable<string> secIds)
    {
        var text = string.Join(",", secIds.Select(DisplayCode));
        _ctx?.Config.Set("codes", text);
    }

    /// <summary>规范化成东方财富 secid：1.xxxxxx（沪）/ 0.xxxxxx（深）。</summary>
    private static string Normalize(string code)
    {
        var c = code.Trim().ToLowerInvariant();
        var market = "";
        if (c.StartsWith("sh")) { market = "1"; c = c[2..]; }
        else if (c.StartsWith("sz")) { market = "0"; c = c[2..]; }
        if (c.Length != 6 || !c.All(char.IsDigit)) return "";
        if (market.Length == 0) market = c[0] is '6' or '9' ? "1" : "0";
        return market + "." + c;
    }

    private static string DisplayCode(string secid)
    {
        var parts = secid.Split('.');
        if (parts.Length != 2) return secid;
        return (parts[0] == "1" ? "sh" : "sz") + parts[1];
    }

    // ---------------- 数据 ----------------

    private sealed class Quote
    {
        public string SecId = "";
        public string Name = "";
        public double Price;
        public double Pct;
        public double Chg;
    }

    private async Task RefreshAsync()
    {
        if (_busy || _ctx == null) return;
        _busy = true;
        try
        {
            var codes = GetCodes();
            var showIndices = _ctx.Config.Get("show_indices", true);

            // 失败冷却：被限流时不要每 15 秒猛打一次，反而更难恢复
            if (_lastFailAt != DateTime.MinValue && (DateTime.Now - _lastFailAt).TotalSeconds < 45
                && _quotes.Count > 0)
            {
                _status = $"更新失败，显示 {_lastSuccess:HH:mm:ss} 的数据（稍后自动重试）";
                return;
            }

            var tasks = new List<Task>();
            if (codes.Count > 0)
                tasks.Add(FetchQuotesAsync(codes, _quotes));
            if (showIndices)
                tasks.Add(FetchQuotesAsync(IndexIds.ToList(), _indices, isIndex: true));

            if (tasks.Count == 0)
            {
                _status = "还没有自选股，点右上角「＋添加」";
            }
            else
            {
                await Task.WhenAll(tasks);
                _status = codes.Count == 0
                    ? $"仅显示大盘指数 · {DateTime.Now:MM-dd HH:mm:ss}"
                    : $"更新于 {DateTime.Now:HH:mm:ss} · {_quotes.Count} 只自选股";
                _lastSuccess = DateTime.Now;
                _lastFailAt = DateTime.MinValue;
                SaveCache();
                CheckAlerts(_quotes);

                // 首次加载默认选中第一只，走势图开箱就有内容
                if (_selected.Length == 0 && _quotes.Count > 0)
                    _selected = _quotes[0].SecId;
            }
        }
        catch (Exception ex)
        {
            _lastFailAt = DateTime.Now;
            _status = _quotes.Count > 0
                ? $"更新失败（显示 {_lastSuccess:HH:mm:ss} 的数据）：{Short(ex)}"
                : "拉取失败：" + Short(ex) + "\n接口可能对当前网络限流，稍后会自动重试。";
            _ctx?.Log.Warn("股票行情拉取失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateUi();
        }

        if (_selected.Length > 0) await RefreshChartAsync();
    }

    private async Task FetchQuotesAsync(List<string> secIds, List<Quote> target, bool isIndex = false)
    {
        var query = $"?fltt=2&secids={string.Join(",", secIds)}&fields=f12,f14,f2,f3,f4";
        var json = await FetchJsonAsync(ListPath, query, history: false);

        using var doc = JsonDocument.Parse(json);
        var list = new List<Quote>();
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("diff", out var diff) &&
            diff.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in diff.EnumerateArray())
            {
                var q = new Quote
                {
                    Name = Str(item, "f14"),
                    Price = Num(item, "f2"),
                    Pct = Num(item, "f3"),
                    Chg = Num(item, "f4"),
                };
                var c = Str(item, "f12");

                // 指数不能按"6 开头=沪"猜市场：上证 1.000001 与深市 000001 同为 6 位数字。
                // 指数量直接按请求里的 secid 后缀匹配回填。
                q.SecId = isIndex
                    ? secIds.FirstOrDefault(id => id.EndsWith("." + c, StringComparison.OrdinalIgnoreCase)) ?? c
                    : (c.Length == 6 ? Normalize(c) : c);

                list.Add(q);
            }
        }

        // 按配置顺序排序（接口不保证顺序）
        var ordered = new List<Quote>();
        foreach (var id in secIds)
        {
            var hit = list.Find(x => string.Equals(x.SecId, id, StringComparison.OrdinalIgnoreCase));
            if (hit != null) ordered.Add(hit);
        }

        target.Clear();
        target.AddRange(ordered);
    }

    /// <summary>拉走势：分时（ndays=1）或日 K（最近 60 根）。</summary>
    private async Task RefreshChartAsync()
    {
        if (_ctx == null || _selected.Length == 0) return;
        try
        {
            List<double> pts = new();
            string label;

            if (_chartMode == "kline")
            {
                var json = await FetchJsonAsync(KlinePath,
                    $"?secid={_selected}&klt=101&fqt=1&fields1=f1,f2,f3&fields2=f51,f53&end=20500101&lmt=60",
                    history: true);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                    d.TryGetProperty("klines", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString() ?? "";
                        var parts = s.Split(',');
                        if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                            pts.Add(v);
                    }
                }
                label = "近 60 日";
            }
            else
            {
                var json = await FetchJsonAsync(TrendPath,
                    $"?secid={_selected}&fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f53&iscr=0&ndays=1",
                    history: true);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                    d.TryGetProperty("trends", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString() ?? "";
                        var parts = s.Split(',');
                        if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                            pts.Add(v);
                    }
                }
                label = "今日分时";
            }

            var name = _quotes.Find(q => q.SecId == _selected)?.Name ?? DisplayCode(_selected);
            var ui = _ctx.Ui;
            ui.BeginInvoke(new Action(() =>
            {
                if (_chartTitle != null) _chartTitle.Text = $"{name} · {label}";
                if (_chart != null)
                {
                    _chart.Points = pts;
                    _chart.InvalidateVisual();
                }
                _ctx?.RequestRepaint();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _ctx.Log.Debug("走势拉取失败：" + ex.Message);
        }
    }

    // ---------------- 磁盘缓存（接口限流时仍能看到上一次数据） ----------------

    private sealed class CacheDto
    {
        public DateTime Ts { get; set; }
        public List<Quote> Quotes { get; set; } = new();
        public List<Quote> Indices { get; set; } = new();
    }

    private string CachePath => Path.Combine(_ctx!.DataDirectory, "cache.json");

    private static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true };

    private void SaveCache()
    {
        try
        {
            if (_ctx == null || _quotes.Count == 0) return;
            var dto = new CacheDto { Ts = DateTime.Now, Quotes = _quotes, Indices = _indices };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(dto, CacheJson));
        }
        catch { }
    }

    private void LoadCache()
    {
        try
        {
            if (_ctx == null || !File.Exists(CachePath)) return;
            var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath), CacheJson);
            if (dto == null) return;
            // 缓存过期（超过 3 天）直接丢弃：网络长期不通时不要拿一个月前的价格冒充当前值
            if ((DateTime.Now - dto.Ts).TotalDays > 3) return;
            _quotes.Clear();
            _quotes.AddRange(dto.Quotes);
            _indices.Clear();
            _indices.AddRange(dto.Indices);
            _lastSuccess = dto.Ts;
            if (_quotes.Count > 0)
            {
                _status = $"显示 {dto.Ts:MM-dd HH:mm:ss} 的缓存数据（正在刷新…）";
                if (_selected.Length == 0) _selected = _quotes[0].SecId;
            }
        }
        catch { }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // 财经接口对无 UA 的请求常直接断开，带上浏览器标识更稳
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36");
            http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }
        catch { }
        return http;
    }

    /// <summary>
    /// 带多域名故障切换的 GET。
    /// 东方财富对单域名有频率限制（表现为连接被重置），换域名往往立刻恢复。
    /// </summary>
    private async Task<string> FetchJsonAsync(string path, string query, bool history)
    {
        var hosts = history ? HistoryHosts : QuoteHosts;
        Exception? last = null;

        foreach (var host in hosts)
        {
            try
            {
                using var resp = await _http.GetAsync(host + path + query);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                last = ex;
                _ctx?.Log.Debug($"请求失败，换域名重试：{host}{path}（{ex.GetType().Name}）");
            }
        }
        throw last ?? new HttpRequestException("所有域名都不可用");
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    private static string Short(Exception ex)
    {
        var m = ex.GetBaseException().Message;
        if (m.Contains("SSL", StringComparison.OrdinalIgnoreCase)) return "网络 TLS 失败";
        if (ex is TaskCanceledException) return "超时";
        return m.Length > 60 ? m[..60] : m;
    }

    /// <summary>涨跌幅超过阈值推一条消息（同股 10 分钟冷却）。</summary>
    private void CheckAlerts(List<Quote> list)
    {
        var threshold = _ctx?.Config.Get("alert_pct", 5.0) ?? 5.0;
        if (threshold <= 0) return;

        foreach (var q in list)
        {
            if (Math.Abs(q.Pct) < threshold) continue;
            if (_lastAlert.TryGetValue(q.SecId, out var at) && (DateTime.Now - at).TotalMinutes < 10) continue;
            _lastAlert[q.SecId] = DateTime.Now;

            var up = q.Pct >= 0;
            _ctx?.PushMessage(new PluginMessage
            {
                Source = "stock",
                SourceName = "股票行情",
                Title = $"{q.Name} {(up ? "上涨" : "下跌")} {Math.Abs(q.Pct):0.00}%",
                Body = $"{DisplayCode(q.SecId)} 现价 {q.Price:0.00}，涨跌 {q.Chg:+0.00;-0.00}",
                Level = up ? "info" : "warn",
                IconGlyph = up ? "\uE74A" : "\uE74B",
                IconColor = up ? "#E8544E" : "#35C98E",
            });
        }
    }

    // ---------------- 页面 ----------------

    private FrameworkElement BuildPage()
    {
        var root = new StackPanel { Margin = new Thickness(4, 2, 4, 0) };

        // 标题行：标题 + 刷新 + 添加
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text("股票行情", "T1", 15, FontWeights.SemiBold));

        var refresh = SmallButton("刷新");
        refresh.Click += (_, _) => _ = RefreshAsync();
        head.Children.Add(refresh);

        var add = SmallButton("＋ 添加");
        add.Click += (_, _) => ToggleAddBox();
        head.Children.Add(add);
        root.Children.Add(head);

        _statusText = Text(_status, "T3", 11.5);
        _statusText.Margin = new Thickness(0, 4, 0, 6);
        root.Children.Add(_statusText);

        // 添加输入框（默认隐藏）
        _addBox = new TextBox { Height = 30, Margin = new Thickness(0, 0, 0, 6) };
        _addBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        _addBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ConfirmAdd();
            else if (e.Key == Key.Escape) ToggleAddBox(false);
        };
        _addHost = new Border { Child = _addBox, Visibility = Visibility.Collapsed };
        root.Children.Add(_addHost);

        // 大盘指数
        _indexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(_indexRow);

        // 自选股列表（限高 + 滚动）
        _rows = new StackPanel();
        var listScroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 132,
        };
        root.Children.Add(listScroll);

        // 走势图区：分时/日K 切换 + 选中标的
        var chartHead = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 4),
        };
        _trendBtn = SmallButton("分时", 0);
        _trendBtn.Click += (_, _) => SetChartMode("trend");
        _klineBtn = SmallButton("日K");
        _klineBtn.Click += (_, _) => SetChartMode("kline");
        chartHead.Children.Add(_trendBtn);
        chartHead.Children.Add(_klineBtn);

        _chartTitle = Text("点一只股票看走势", "T3", 11.5);
        _chartTitle.Margin = new Thickness(8, 0, 0, 0);
        chartHead.Children.Add(_chartTitle);
        root.Children.Add(chartHead);

        _chart = new MiniChart { Height = 92 };
        root.Children.Add(_chart);

        UpdateUi();
        return root;
    }

    private void ToggleAddBox(bool? show = null)
    {
        if (_addHost == null || _addBox == null) return;
        var visible = show ?? _addHost.Visibility != Visibility.Visible;
        _addHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            _addBox.Text = "";
            _addBox.Focus();
            Keyboard.Focus(_addBox);
        }
        _ctx?.RequestRepaint();
    }

    private void ConfirmAdd()
    {
        if (_addBox == null) return;
        var secid = Normalize(_addBox.Text);
        if (secid.Length == 0)
        {
            _status = "代码格式不对，示例：sh600000 / 600519";
            UpdateUi();
            return;
        }

        var codes = GetCodes();
        if (codes.Contains(secid, StringComparer.OrdinalIgnoreCase))
        {
            _status = "该代码已在自选里";
            UpdateUi();
            return;
        }
        if (codes.Count >= MaxCodes)
        {
            _status = $"最多 {MaxCodes} 只，先删掉一些";
            UpdateUi();
            return;
        }

        codes.Add(secid);
        SaveCodes(codes);
        _selected = secid;
        ToggleAddBox(false);
        _status = "已添加，正在刷新…";
        UpdateUi();
        _ = RefreshAsync();
    }

    private void RemoveCode(string secid)
    {
        var codes = GetCodes();
        codes.RemoveAll(c => string.Equals(c, secid, StringComparison.OrdinalIgnoreCase));
        SaveCodes(codes);
        if (_selected == secid)
        {
            _selected = "";
            if (_chart != null) { _chart.Points = new List<double>(); _chart.InvalidateVisual(); }
            if (_chartTitle != null) _chartTitle.Text = "点一只股票看走势";
        }
        _status = "已移除，正在刷新…";
        UpdateUi();
        _ = RefreshAsync();
    }

    private void SelectStock(string secid)
    {
        _selected = secid;
        var name = _quotes.Find(q => q.SecId == secid)?.Name ?? DisplayCode(secid);
        if (_chartTitle != null) _chartTitle.Text = $"{name} · 加载中…";
        UpdateUi();
        _ = RefreshChartAsync();
    }

    private void SetChartMode(string mode)
    {
        _chartMode = mode;
        _ctx?.Config.Set("chart_mode", mode);
        UpdateUi();
        _ = RefreshChartAsync();
    }

    private void UpdateUi()
    {
        var ui = _ctx?.Ui;
        if (ui == null) return;
        if (!ui.CheckAccess())
        {
            ui.BeginInvoke(new Action(UpdateUi), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        if (_statusText != null) _statusText.Text = _status;

        // 分时/日K 按钮高亮
        StyleModeButton(_trendBtn, _chartMode == "trend");
        StyleModeButton(_klineBtn, _chartMode == "kline");

        // 指数
        if (_indexRow != null)
        {
            _indexRow.Children.Clear();
            foreach (var q in _indices)
            {
                var up = q.Pct >= 0;
                var chip = new Border
                {
                    Padding = new Thickness(9, 4, 9, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                    CornerRadius = new CornerRadius(9),
                    Background = Brush("AppCard"),
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(Text(q.Name, "T3", 11.5));
                var pct = Text($" {q.Price:0.00} ", null, 11.5, FontWeights.SemiBold);
                pct.Foreground = Brush("AppText");
                sp.Children.Add(pct);
                var delta = Text($"{q.Pct:+0.00;-0.00}%", null, 11.5, FontWeights.SemiBold);
                delta.Foreground = Brush(up ? "AppUp" : "AppDown");
                sp.Children.Add(delta);
                chip.Child = sp;
                _indexRow.Children.Add(chip);
            }
        }

        // 自选股列表
        if (_rows != null)
        {
            _rows.Children.Clear();
            foreach (var q in _quotes)
            {
                var selected = string.Equals(q.SecId, _selected, StringComparison.OrdinalIgnoreCase);
                var row = new Border
                {
                    Padding = new Thickness(10, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 4),
                    CornerRadius = new CornerRadius(10),
                    Background = Brush(selected ? "AppPill" : "AppCard"),
                    Cursor = Cursors.Hand,
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                var nameText = Text(q.Name.Length > 0 ? q.Name : DisplayCode(q.SecId), "T2", 13, FontWeights.SemiBold);
                if (selected) nameText.SetResourceReference(TextBlock.ForegroundProperty, "AppPillFg");
                left.Children.Add(nameText);
                var codeLine = Text(DisplayCode(q.SecId), "T3", 10.5);
                codeLine.Margin = new Thickness(0, 0, 0, 0);
                if (selected) codeLine.SetResourceReference(TextBlock.ForegroundProperty, "AppPillFg");
                left.Children.Add(codeLine);
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var price = Text(q.Price > 0 ? q.Price.ToString("0.00") : "--", "T2", 14, FontWeights.SemiBold);
                price.VerticalAlignment = VerticalAlignment.Center;
                price.Margin = new Thickness(8, 0, 8, 0);
                if (selected) price.SetResourceReference(TextBlock.ForegroundProperty, "AppPillFg");
                Grid.SetColumn(price, 1);
                grid.Children.Add(price);

                var up = q.Pct >= 0;
                var pct = Text(q.Pct == 0 ? "0.00%" : $"{q.Pct:+0.00;-0.00}%", null, 12.5, FontWeights.SemiBold);
                pct.Foreground = selected ? Brush("AppPillFg") : Brush(up ? "AppUp" : "AppDown");
                pct.HorizontalAlignment = HorizontalAlignment.Right;
                pct.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pct, 2);
                grid.Children.Add(pct);

                var del = new Button
                {
                    Content = "×",
                    Width = 20,
                    Height = 20,
                    FontSize = 13,
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FocusVisualStyle = null,
                    Cursor = Cursors.Hand,
                    ToolTip = "从自选里移除",
                };
                del.SetResourceReference(Control.ForegroundProperty, selected ? "AppPillFg" : "AppText3");
                var capturedRemove = q.SecId;
                del.Click += (_, _) => RemoveCode(capturedRemove);
                Grid.SetColumn(del, 3);
                grid.Children.Add(del);

                row.Child = grid;
                var capturedSelect = q.SecId;
                row.MouseLeftButtonUp += (_, _) => SelectStock(capturedSelect);
                _rows.Children.Add(row);
            }
        }

        // 插件改了视觉树 → 请宿主重绘
        _ctx?.RequestRepaint();
    }

    private void StyleModeButton(Button? btn, bool active)
    {
        if (btn == null) return;
        if (active)
        {
            btn.SetResourceReference(Control.BackgroundProperty, "AppPill");
            btn.SetResourceReference(Control.ForegroundProperty, "AppPillFg");
        }
        else
        {
            btn.SetResourceReference(Control.BackgroundProperty, "AppCard");
            btn.SetResourceReference(Control.ForegroundProperty, "AppText2");
        }
    }

    // ---------------- 设置面板 ----------------

    private FrameworkElement BuildSettings()
    {
        var root = new StackPanel();

        root.Children.Add(Text("自选股代码（逗号分隔，sh/sz 可省略）", "T3", 11.5));
        var codes = new TextBox
        {
            Text = _ctx?.Config.Get("codes", "sh600000,sz000001,sz300750") ?? "",
            Height = 34,
            Margin = new Thickness(0, 6, 0, 0),
        };
        codes.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        codes.LostKeyboardFocus += (_, _) =>
        {
            _ctx?.Config.Set("codes", codes.Text.Trim());
            _ = RefreshAsync();
        };
        root.Children.Add(codes);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(Text("刷新间隔(秒)", "T3", 11.5));
        var interval = new TextBox
        {
            Text = (_ctx?.Config.Get("refresh_sec", 60) ?? 60).ToString(),
            Width = 64,
            Height = 30,
            Margin = new Thickness(8, 0, 16, 0),
        };
        interval.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        row.Children.Add(interval);

        row.Children.Add(Text("提醒阈值(%)", "T3", 11.5));
        var alert = new TextBox
        {
            Text = (_ctx?.Config.Get("alert_pct", 5.0) ?? 5.0).ToString("0.#"),
            Width = 64,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
        };
        alert.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        row.Children.Add(alert);
        root.Children.Add(row);

        var save = new Button
        {
            Content = "保存并立即刷新",
            Height = 28,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 0, 12, 0),
        };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        save.Click += (_, _) =>
        {
            _ctx?.Config.Set("codes", codes.Text.Trim());
            if (int.TryParse(interval.Text.Trim(), out var sec)) _ctx?.Config.Set("refresh_sec", Math.Max(15, sec));
            if (double.TryParse(alert.Text.Trim(), out var pct)) _ctx?.Config.Set("alert_pct", Math.Max(0, pct));
            _ctx?.Log.Info("股票设置已保存");
            _ = RefreshAsync();
        };
        root.Children.Add(save);

        root.Children.Add(Text("数据来自东方财富公开接口，仅供参考，不构成投资建议。", "T3", 11));
        return root;
    }

    // ---------------- UI 小工具 ----------------

    private Button SmallButton(string label, double leftMargin = 12)
    {
        var b = new Button
        {
            Content = label,
            Height = 24,
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        return b;
    }

    private TextBlock Text(string text, string? style, double size, FontWeight? weight = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (weight.HasValue) tb.FontWeight = weight.Value;
        if (style != null)
        {
            try { tb.SetResourceReference(FrameworkElement.StyleProperty, style); } catch { }
        }
        else
        {
            tb.SetResourceReference(TextBlock.ForegroundProperty, "AppText");
        }
        return tb;
    }

    private static Brush Brush(string key)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return Brushes.Gray;
    }

    /// <summary>迷你走势图：折线 + 渐变填充 + 首值虚线基准。</summary>
    private sealed class MiniChart : FrameworkElement
    {
        public List<double> Points = new();

        /// <summary>取主题色（取不到就用兜底色），并套上指定透明度。</summary>
        private static Color ChartColor(string key, Color fallback, byte alpha)
        {
            var c = Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var h = ActualHeight;
            if (w < 8 || h < 8) return;

            // 配色跟随主题（浅色主题下不能再写死白色系，否则整块图看不清）
            var panel = ChartColor("AppTrack", Color.FromArgb(255, 255, 255, 255), 18);
            var textColor = ChartColor("AppText3", Color.FromArgb(255, 160, 160, 160), 210);
            var gridColor = ChartColor("AppText3", Color.FromArgb(255, 160, 160, 160), 70);

            // 背景
            dc.DrawRoundedRectangle(new SolidColorBrush(panel), null, new Rect(0, 0, w, h), 8, 8);

            if (Points.Count < 2)
            {
                var tb = new FormattedText("暂无走势数据",
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal,
                        FontWeights.Normal, FontStretches.Normal), 11,
                    new SolidColorBrush(textColor), 1.0);
                dc.DrawText(tb, new Point(10, h / 2 - tb.Height / 2));
                return;
            }

            var min = Points.Min();
            var max = Points.Max();
            if (Math.Abs(max - min) < 1e-9) { max = min + 1; }
            var pad = 6.0;
            var innerW = w - pad * 2;
            var innerH = h - pad * 2;

            double Y(double v) => pad + innerH * (1 - (v - min) / (max - min));
            double X(int i) => pad + innerW * i / (Points.Count - 1);

            // 首值基准虚线
            var baseY = Y(Points[0]);
            var basePen = new Pen(new SolidColorBrush(gridColor), 1)
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
            };
            dc.DrawLine(basePen, new Point(pad, baseY), new Point(w - pad, baseY));

            var up = Points[^1] >= Points[0];
            var color = up
                ? (Application.Current?.TryFindResource("AppUp") as SolidColorBrush)?.Color ?? Color.FromRgb(232, 84, 78)
                : (Application.Current?.TryFindResource("AppDown") as SolidColorBrush)?.Color ?? Color.FromRgb(53, 201, 142);

            // 折线
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(Points[0])), false, false);
                for (var i = 1; i < Points.Count; i++)
                    ctx.LineTo(new Point(X(i), Y(Points[i])), true, false);
            }
            geo.Freeze();
            var linePen = new Pen(new SolidColorBrush(color), 1.4) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, linePen, geo);

            // 渐变填充（折线下方）
            var fillGeo = new StreamGeometry();
            using (var ctx = fillGeo.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(Points[0])), true, true);
                for (var i = 1; i < Points.Count; i++)
                    ctx.LineTo(new Point(X(i), Y(Points[i])), true, false);
                ctx.LineTo(new Point(X(Points.Count - 1), h - pad), true, false);
                ctx.LineTo(new Point(X(0), h - pad), true, false);
            }
            fillGeo.Freeze();

            var fill = new LinearGradientBrush(
                Color.FromArgb(64, color.R, color.G, color.B),
                Color.FromArgb(0, color.R, color.G, color.B),
                new Point(0, 0), new Point(0, 1));
            dc.DrawGeometry(fill, null, fillGeo);

            // 最新点
            dc.DrawEllipse(new SolidColorBrush(color), null,
                new Point(X(Points.Count - 1), Y(Points[^1])), 2.6, 2.6);
        }
    }
}