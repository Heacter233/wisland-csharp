using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisland.Plugins;
using Wisland.Services;

namespace Ff14TreasurePlugin;

/// <summary>
/// FF14 挖宝悬浮窗（灵动岛版）。
///
/// 数据来源：ACT 插件 FF14TreasureHunterACT 自带的 OverlayServer：
///   - WebSocket `ws://127.0.0.1:{overlay_port}/ws`（默认 8765），每秒推一份 summary + 待推送通知；
///   - 本机回环请求免鉴权，所以插件只要连上就能拿到全部数据，**不需要改动 ACT 项目**。
///
/// summary 字段：
///   run_label / maps / magic_seals / system_gold / total_income / total_cost / profit / segments /
///   player / world / map / party_count / items[{map,name,qty}] / layers[{name,count}] / customs[{name,total}]
///   notifications[{type:"success"|"fail"|"info", text, timestamp}]
///
/// 展示：
///   - 插件页 = 灵动岛版悬浮窗：统计卡 + 物品出货（按地图分组）+ 层级/其他记录；
///   - 鉴定成功 / 失败：用 NotifyDraw 自绘「开箱发光 / 箱子打叉」两个图案，推到消息通知栏；
///   - 获得物品：合并成一条「本轮出货」通知（默认攒 4 秒），明细在悬浮窗里按地图→物品分层看。
/// </summary>
public sealed class Ff14TreasurePlugin : IWislandPlugin
{
    private const string PluginId = "ff14-treasure";
    private const string SourceName = "挖宝悬浮窗";

    private IPluginContext? _ctx;

    // ---------------- 连接 ----------------
    private CancellationTokenSource? _cts;
    private bool _connected;
    private string _status = "未连接";
    private DateTime _connectedAt = DateTime.MinValue;
    private DateTime _lastDataAt = DateTime.MinValue;

    // ---------------- 数据 ----------------
    private sealed class LootItem
    {
        public string Map { get; init; } = "";
        public string Name { get; init; } = "";
        public long Qty { get; init; }
    }

    private sealed class LayerRow
    {
        public string Name { get; init; } = "";
        public long Count { get; init; }
    }

    private sealed class CustomRow
    {
        public string Name { get; init; } = "";
        public long Total { get; init; }
    }

    private sealed class Snapshot
    {
        public string RunLabel = "";
        public long Maps;
        public long MagicSeals;
        // ACT 推的是浮点数（system_gold 是金/成本/收入/盈亏可能是小数）
        public double SystemGold;
        public double TotalIncome;
        public double TotalCost;
        public double Profit;
        public int Segments;
        public string Player = "";
        public string World = "";
        public string CurrentMap = "";
        public int PartyCount;
        public List<LootItem> Items = new();
        public List<LayerRow> Layers = new();
        public List<CustomRow> Customs = new();
    }

    private Snapshot _snapshot = new();
    private readonly object _lock = new();

    // 上一份 items 的累计数量（key = "地图\u0001物品名"），用于做差得到"新获得"
    private Dictionary<string, long> _prevItems = new(StringComparer.Ordinal);
    private bool _primed;

    // 待合并的出货通知
    private readonly List<LootItem> _pendingLoot = new();
    private DateTime _lastLootChange = DateTime.MinValue;

    // ---------------- 页面控件 ----------------
    private TextBlock? _statusText;
    private TextBlock? _runText;
    private TextBlock? _mapText;
    private TextBlock? _partyText;
    private UniformGrid? _statPanel;
    private StackPanel? _itemsPanel;
    private StackPanel? _layersPanel;
    private TextBlock? _footerText;
    private DispatcherTimer? _uiTick;

    // ---------------- 生命周期 ----------------

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;

        context.RegisterPage(new PluginPageDescriptor
        {
            Key = PluginId,
            Title = "挖宝悬浮窗",
            Glyph = "\uE7B8",
            Height = 500,
            Build = BuildPage,
        });
        context.RegisterSettingsSection("FF14 挖宝悬浮窗", "连接 ACT 挖宝会计的悬浮窗服务", BuildSettings);

        // 每秒：处理合并出货通知 / 刷新"未连接"状态文案
        context.RegisterTimer(TimeSpan.FromSeconds(1), Tick);

        StartConnection();

        context.Log.Info("FF14 挖宝悬浮窗已加载，准备连接 ACT OverlayServer");
    }

    public void OnUnload()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        try { _uiTick?.Stop(); } catch { }
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

    private void SetCfg(string key, object value)
    {
        try { _ctx?.Config.Set(key, value); } catch { }
    }

    private string WsHost => (Cfg("host", "127.0.0.1") ?? "127.0.0.1").Trim();
    private int WsPort => Math.Clamp(Cfg("port", 8765), 1, 65535);
    private bool NotifyPredictions => Cfg("notify_predictions", true);
    private bool NotifyItems => Cfg("notify_items", true);
    private int MergeSeconds => Math.Clamp(Cfg("item_merge_seconds", 4), 1, 30);
    private int ReconnectSeconds => Math.Clamp(Cfg("reconnect_seconds", 5), 1, 60);

    // ---------------- WebSocket ----------------

    private void StartConnection()
    {
        var ctx = _ctx;
        if (ctx == null) return;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.ShutdownToken);
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                SetStatus($"连接 {WsHost}:{WsPort} …");
                UpdateUi();

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    await ws.ConnectAsync(new Uri($"ws://{WsHost}:{WsPort}/ws"), timeout.Token).ConfigureAwait(false);
                }

                _connected = true;
                _connectedAt = DateTime.Now;
                SetStatus($"已连接 {WsHost}:{WsPort}");
                UpdateUi();
                _ctx?.Log.Info($"已连接 ACT 悬浮窗服务 ws://{WsHost}:{WsPort}/ws");

                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _ctx?.Log.Debug("WS 连接异常：" + ex.Message);
            }
            finally
            {
                _connected = false;
                _connectedAt = DateTime.MinValue;
                UpdateUi();
            }

            if (ct.IsCancellationRequested) break;
            SetStatus($"未连接，{ReconnectSeconds} 秒后重试（ACT 里需开启悬浮窗服务）");
            UpdateUi();
            try { await Task.Delay(TimeSpan.FromSeconds(ReconnectSeconds), ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 8 * 1024 * 1024) return;   // 防御：异常大包直接断开重连
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(ms.ToArray());
            _lastDataAt = DateTime.Now;
            try { HandlePayload(json); }
            catch (Exception ex) { _ctx?.Log.Debug("解析 summary 失败：" + ex.Message); }
        }
    }

    // ---------------- 数据处理 ----------------

    private void HandlePayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        var snap = new Snapshot();
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "run_label": snap.RunLabel = Str(p.Value); break;
                case "maps": snap.Maps = Long(p.Value); break;
                case "magic_seals": snap.MagicSeals = Long(p.Value); break;
                case "system_gold": snap.SystemGold = Double(p.Value); break;
                case "total_income": snap.TotalIncome = Double(p.Value); break;
                case "total_cost": snap.TotalCost = Double(p.Value); break;
                case "profit": snap.Profit = Double(p.Value); break;
                case "segments": snap.Segments = (int)Long(p.Value); break;
                case "player": snap.Player = Str(p.Value); break;
                case "world": snap.World = Str(p.Value); break;
                case "map": snap.CurrentMap = Str(p.Value); break;
                case "party_count": snap.PartyCount = (int)Long(p.Value); break;
                case "items":
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var e in p.Value.EnumerateArray())
                            snap.Items.Add(new LootItem
                            {
                                Map = Prop(e, "map"),
                                Name = Prop(e, "name"),
                                Qty = Long(PropRaw(e, "qty")),
                            });
                    break;
                case "layers":
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var e in p.Value.EnumerateArray())
                            snap.Layers.Add(new LayerRow { Name = Prop(e, "name"), Count = Long(PropRaw(e, "count")) });
                    break;
                case "customs":
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var e in p.Value.EnumerateArray())
                            snap.Customs.Add(new CustomRow { Name = Prop(e, "name"), Total = Long(PropRaw(e, "total")) });
                    break;
            }
        }

        lock (_lock) _snapshot = snap;

        DetectNewLoot(snap);
        HandleNotifications(root);

        UpdateUi();
    }

    /// <summary>对比上一份 items 累计数量，算出"新获得"的物品并攒进待推送队列。</summary>
    private void DetectNewLoot(Snapshot snap)
    {
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var it in snap.Items)
            current[it.Map + "\u0001" + it.Name] = it.Qty;

        if (!_primed)
        {
            // 首次连上：只记录基线，不推送历史出货
            _prevItems = current;
            _primed = true;
            return;
        }

        // 新一轮（累计值整体变小）→ 重置基线，不推送
        var reset = current.Count > 0 &&
                    _prevItems.Count > 0 &&
                    current.All(kv => !_prevItems.TryGetValue(kv.Key, out var old) || kv.Value < old);
        if (reset)
        {
            _prevItems = current;
            return;
        }

        var newOnes = new List<LootItem>();
        foreach (var it in snap.Items)
        {
            var key = it.Map + "\u0001" + it.Name;
            var old = _prevItems.TryGetValue(key, out var v) ? v : 0;
            if (it.Qty > old)
                newOnes.Add(new LootItem { Map = it.Map, Name = it.Name, Qty = it.Qty - old });
        }
        _prevItems = current;

        if (newOnes.Count == 0) return;
        lock (_lock)
        {
            _pendingLoot.AddRange(newOnes);
            _lastLootChange = DateTime.Now;
        }
    }

    private void HandleNotifications(JsonElement root)
    {
        if (!root.TryGetProperty("notifications", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var n in arr.EnumerateArray())
        {
            var type = Prop(n, "type");
            var text = Prop(n, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!NotifyPredictions) continue;
            PushPrediction(type, text);
        }
    }

    /// <summary>把待推送的出货合并成一条通知（停止变化 MergeSeconds 秒后发）。</summary>
    private void Tick()
    {
        List<LootItem> loot;
        lock (_lock)
        {
            if (_pendingLoot.Count == 0) return;
            if ((DateTime.Now - _lastLootChange).TotalSeconds < MergeSeconds) return;
            loot = _pendingLoot.ToList();
            _pendingLoot.Clear();
        }
        if (!NotifyItems) return;
        FlushLoot(loot);
        UpdateUi();
    }

    // ---------------- 通知推送 ----------------

    private void PushPrediction(string type, string text)
    {
        var ctx = _ctx;
        if (ctx == null) return;

        var success = string.Equals(type, "success", StringComparison.OrdinalIgnoreCase);
        var fail = string.Equals(type, "fail", StringComparison.OrdinalIgnoreCase);

        var title = success ? "鉴定成功" : fail ? "鉴定失败" : "挖宝提示";
        var icon = success ? "\uE73E" : fail ? "\uE711" : "\uE946";
        var color = success ? "#4CAF50" : fail ? "#E05252" : "#5AA9E6";

        try
        {
            ctx.PushMessage(new PluginMessage
            {
                // Source 必须等于插件 id：宿主按它查 notify.sound 声明 / 来源开关
                Source = PluginId,
                SourceName = SourceName,
                Title = title,
                Body = text,
                Level = fail ? "warn" : "info",
                IconGlyph = icon,
                IconColor = color,
                NotifyDraw = success ? DrawChestOpen : fail ? DrawChestFail : null,
            });
        }
        catch (Exception ex)
        {
            ctx.Log.Error("推送鉴定通知失败", ex);
        }
        ctx.Log.Debug($"通知：{type} · {text}");
    }

    private void FlushLoot(List<LootItem> loot)
    {
        var ctx = _ctx;
        if (ctx == null || loot.Count == 0) return;

        var total = loot.Sum(x => x.Qty);
        var distinct = loot.Select(x => x.Name).Distinct().Count();

        // 正文：列出前几件（按地图分组前缀），剩下的用 +N 收口
        var parts = loot
            .OrderByDescending(x => x.Qty)
            .Take(4)
            .Select(x => string.IsNullOrWhiteSpace(x.Map) ? $"{x.Name} ×{x.Qty}" : $"{x.Name} ×{x.Qty}")
            .ToList();
        var body = string.Join(" · ", parts);
        if (loot.Count > 4) body += $" · 等 {loot.Count} 项";

        var title = $"本轮出货 · 共 {total} 件";
        if (distinct > 1) title += $"（{distinct} 种）";

        try
        {
            ctx.PushMessage(new PluginMessage
            {
                Source = PluginId,
                SourceName = SourceName,
                Title = title,
                Body = body,
                Level = "info",
                IconGlyph = "\uE7B8",
                IconColor = "#C9A227",
            });
        }
        catch (Exception ex)
        {
            ctx.Log.Error("推送出货通知失败", ex);
        }
        ctx.Log.Info($"出货通知：{title} · {body}");
    }

    // ---------------- 自绘图案：开箱发光 / 箱子打叉 ----------------

    private static readonly Color GoldColor = Color.FromRgb(0xC9, 0xA2, 0x27);
    private static readonly Color GoldLightColor = Color.FromRgb(0xFF, 0xD7, 0x5E);
    private static readonly Color ChestDarkColor = Color.FromRgb(0x5A, 0x42, 0x18);
    private static readonly Color FailColor = Color.FromRgb(0xE0, 0x52, 0x52);

    /// <summary>
    /// 成功：打开的宝箱 + 金光 + 光晕（按经过时间做呼吸动画）。
    /// 可用区域约 108×20，所以图形按高度自适应、水平居中。
    /// </summary>
    private static void DrawChestOpen(DrawingContext dc, Rect area, double t)
    {
        try
        {
            var s = Math.Min(area.Height, 22) * 0.92;
            if (s < 6) return;
            var w = s * 1.25;
            var h = s;
            var left = area.X + (area.Width - w) / 2;
            var top = area.Y + (area.Height - h) / 2;
            var cx = left + w / 2;

            var pulse = 0.5 + 0.5 * Math.Abs(Math.Sin(t * 2.4));

            var gold = new SolidColorBrush(GoldColor);
            var goldLight = new SolidColorBrush(GoldLightColor);
            var dark = new SolidColorBrush(ChestDarkColor);
            var glowBrush = new RadialGradientBrush(
                Color.FromArgb((byte)(110 * pulse), 0xFF, 0xE9, 0x9A),
                Color.FromArgb(0, 0xFF, 0xE9, 0x9A))
            {
                RadiusX = 0.9,
                RadiusY = 0.9,
            };
            gold.Freeze(); goldLight.Freeze(); dark.Freeze(); glowBrush.Freeze();

            // 光晕
            dc.DrawEllipse(glowBrush, null, new Point(cx, top + h * 0.42), w * 0.85, h * 0.85);

            // 放射光线
            var rayPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(150 * pulse), 0xFF, 0xD7, 0x5E)), 1.0);
            rayPen.Freeze();
            var rayLen = h * (0.42 + 0.18 * pulse);
            for (var i = 0; i < 6; i++)
            {
                var angle = -Math.PI / 2 + (i - 2.5) * 0.42;
                var x1 = cx + Math.Cos(angle) * w * 0.34;
                var y1 = top + h * 0.40 + Math.Sin(angle) * h * 0.28;
                var x2 = cx + Math.Cos(angle) * (w * 0.34 + rayLen);
                var y2 = top + h * 0.40 + Math.Sin(angle) * (h * 0.28 + rayLen);
                dc.DrawLine(rayPen, new Point(x1, y1), new Point(x2, y2));
            }

            // 箱体（下半）
            var bodyRect = new Rect(left, top + h * 0.46, w, h * 0.5);
            dc.DrawRoundedRectangle(dark, null, bodyRect, 1.5, 1.5);
            dc.DrawRoundedRectangle(gold, new Pen(goldLight, 0.8),
                new Rect(bodyRect.X, bodyRect.Y, bodyRect.Width, bodyRect.Height * 0.42), 1.5, 1.5);

            // 打开的盖子（上方，略后仰）
            var lidRect = new Rect(left + w * 0.03, top + h * 0.06, w * 0.94, h * 0.26);
            dc.PushTransform(new RotateTransform(-16, cx, lidRect.Y + lidRect.Height));
            dc.DrawRoundedRectangle(gold, new Pen(goldLight, 0.8), lidRect, 2, 2);
            dc.Pop();

            // 箱口内发光
            dc.DrawEllipse(
                new SolidColorBrush(Color.FromArgb((byte)(170 * pulse), 0xFF, 0xF0, 0xA8)),
                null, new Point(cx, top + h * 0.47), w * 0.30, h * 0.10);

            // 小星点
            var starBrush = new SolidColorBrush(Color.FromArgb((byte)(200 * pulse), 0xFF, 0xFF, 0xFF));
            starBrush.Freeze();
            var starR = Math.Max(0.7, h * 0.045);
            dc.DrawEllipse(starBrush, null, new Point(left + w * 0.12, top + h * 0.22), starR, starR);
            dc.DrawEllipse(starBrush, null, new Point(left + w * 0.86, top + h * 0.30), starR * 0.8, starR * 0.8);
        }
        catch { }
    }

    /// <summary>失败：关着的宝箱 + 红色叉号（轻微横向抖动）。</summary>
    private static void DrawChestFail(DrawingContext dc, Rect area, double t)
    {
        try
        {
            var s = Math.Min(area.Height, 22) * 0.92;
            if (s < 6) return;
            var w = s * 1.15;
            var h = s;
            var shake = Math.Sin(t * 14) * 0.8;
            var left = area.X + (area.Width - w) / 2 + shake;
            var top = area.Y + (area.Height - h) / 2;
            var cx = left + w / 2;

            var wood = new SolidColorBrush(Color.FromRgb(0x6B, 0x54, 0x3A));
            var woodEdge = new SolidColorBrush(Color.FromRgb(0x3E, 0x30, 0x22));
            var red = new SolidColorBrush(FailColor);
            wood.Freeze(); woodEdge.Freeze(); red.Freeze();

            // 箱体
            var bodyRect = new Rect(left, top + h * 0.40, w, h * 0.56);
            dc.DrawRoundedRectangle(wood, new Pen(woodEdge, 0.8), bodyRect, 1.5, 1.5);

            // 盖子（关着，压在箱体上方）
            var lidRect = new Rect(left, top + h * 0.22, w, h * 0.24);
            dc.DrawRoundedRectangle(wood, new Pen(woodEdge, 0.8), lidRect, 1.5, 1.5);

            // 锁扣/缝线
            var seamPen = new Pen(woodEdge, 0.8);
            seamPen.Freeze();
            dc.DrawLine(seamPen, new Point(left, top + h * 0.46), new Point(left + w, top + h * 0.46));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x2A, 0x20)),
                new Pen(woodEdge, 0.6), new Rect(cx - w * 0.07, top + h * 0.42, w * 0.14, h * 0.16));

            // 红色叉号（画在箱子上方，更醒目）
            var xPen = new Pen(red, Math.Max(1.4, h * 0.10))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            xPen.Freeze();
            var xSize = h * 0.30;
            var xCenter = new Point(cx, top + h * 0.16);
            dc.DrawLine(xPen, new Point(xCenter.X - xSize, xCenter.Y - xSize),
                new Point(xCenter.X + xSize, xCenter.Y + xSize));
            dc.DrawLine(xPen, new Point(xCenter.X + xSize, xCenter.Y - xSize),
                new Point(xCenter.X - xSize, xCenter.Y + xSize));
        }
        catch { }
    }

    // ---------------- JSON 辅助 ----------------

    private static string Prop(JsonElement e, string name) =>
        PropRaw(e, name).ValueKind == JsonValueKind.String ? PropRaw(e, name).GetString() ?? "" : "";

    private static JsonElement PropRaw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string Str(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind == JsonValueKind.Number ? e.ToString() : "";

    private static double Double(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            if (e.TryGetDouble(out var d)) return d;
            if (e.TryGetInt64(out var n)) return n;
        }
        if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), out var s)) return s;
        return 0;
    }

    private static long Long(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s)) return s;
        return 0;
    }

    // ---------------- 页面 ----------------

    private FrameworkElement BuildPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 顶栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 统计卡
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 滚动内容
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 底部状态

        // ---- 顶栏 ----
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 0) };
        head.Children.Add(Text("挖宝悬浮窗", "T1", 15, FontWeights.SemiBold));
        _statusText = Text("", "T3", 10.5, margin: new Thickness(10, 2, 0, 0));
        head.Children.Add(_statusText);

        var reconnect = SmallButton("重连", 10);
        reconnect.Click += (_, _) =>
        {
            SetStatus("手动重连…");
            StartConnection();
        };
        head.Children.Add(reconnect);

        _runText = Text("", "T3", 10.5, margin: new Thickness(10, 2, 0, 0));
        head.Children.Add(_runText);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ---- 统计卡 ----
        _statPanel = new UniformGrid { Rows = 1, Margin = new Thickness(4, 8, 4, 0) };
        Grid.SetRow(_statPanel, 1);
        root.Children.Add(_statPanel);

        // ---- 滚动内容：物品出货 + 层级/其他 ----
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(4, 8, 4, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var content = new StackPanel();

        var itemsHead = new StackPanel { Orientation = Orientation.Horizontal };
        itemsHead.Children.Add(Text("物品出货", "T2", 12, FontWeights.SemiBold));
        _mapText = Text("", "T3", 10.5, margin: new Thickness(8, 1, 0, 0));
        itemsHead.Children.Add(_mapText);
        _partyText = Text("", "T3", 10.5, margin: new Thickness(8, 1, 0, 0));
        itemsHead.Children.Add(_partyText);
        content.Children.Add(itemsHead);

        _itemsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        content.Children.Add(_itemsPanel);

        content.Children.Add(Text("层级 / 其他记录", "T2", 12, FontWeights.SemiBold,
            margin: new Thickness(0, 12, 0, 0)));
        _layersPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        content.Children.Add(_layersPanel);

        scroll.Content = content;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        // ---- 底部状态 ----
        _footerText = Text("等待 ACT 挖宝会计推送数据…", "T3", 10.5, margin: new Thickness(4, 6, 4, 2));
        _footerText.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_footerText, 3);
        root.Children.Add(_footerText);

        RefreshUi();
        return root;
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

    private void RefreshUi()
    {
        if (_statusText != null)
        {
            _statusText.Text = _connected
                ? $"● 已连接（{WsHost}:{WsPort}）"
                : $"○ {_status}";
            try
            {
                _statusText.SetResourceReference(TextBlock.ForegroundProperty,
                    _connected ? "AppUp" : "AppText3");
            }
            catch { }
        }

        Snapshot snap;
        lock (_lock) snap = _snapshot;

        if (_runText != null)
        {
            _runText.Text = string.IsNullOrWhiteSpace(snap.RunLabel)
                ? ""
                : $"轮次：{snap.RunLabel}" + (snap.Segments > 0 ? $" · 分段 {snap.Segments}" : "");
        }

        RefreshStats(snap);

        if (_mapText != null)
            _mapText.Text = string.IsNullOrWhiteSpace(snap.CurrentMap) ? "" : $"当前地图：{snap.CurrentMap}";
        if (_partyText != null)
            _partyText.Text = snap.PartyCount > 0 ? $"队伍 {snap.PartyCount} 人" : "";

        RefreshItems(snap);
        RefreshLayers(snap);

        if (_footerText != null)
        {
            _footerText.Text = _lastDataAt == DateTime.MinValue
                ? (_connected ? "已连接，等待数据…" : "未连接：请确认 ACT 里已开启悬浮窗服务（overlay_enabled）")
                : $"最后数据：{_lastDataAt:HH:mm:ss}" + (snap.Player.Length > 0 ? $" · {snap.Player}{snap.World}" : "");
        }
    }

    private void RefreshStats(Snapshot s)
    {
        if (_statPanel == null) return;
        _statPanel.Children.Clear();

        // 格式对齐原 overlay.html：系统金 ÷10000 显示「万」，收支/盈亏保留两位小数
        _statPanel.Children.Add(StatCard("地图", s.Maps.ToString("N0"), null));
        _statPanel.Children.Add(StatCard("魔纹", s.MagicSeals.ToString("N0"), null));
        _statPanel.Children.Add(StatCard("系统金", (s.SystemGold / 10000.0).ToString("0.0000") + "万", null));
        // 财富悬浮窗的惯例是「绿赚红亏」（与国际行情一致）；
        // 而 AppUp = 红、AppDown = 绿（股票习惯），所以这里刻意反着用。
        _statPanel.Children.Add(StatCard("收入", s.TotalIncome.ToString("N2"), "AppDown"));
        _statPanel.Children.Add(StatCard("成本", s.TotalCost.ToString("N2"), "AppUp"));
        _statPanel.Children.Add(StatCard("盈亏",
            (s.Profit >= 0 ? "+" : "") + s.Profit.ToString("N2"),
            s.Profit >= 0 ? "AppDown" : "AppUp"));
    }

    private FrameworkElement StatCard(string label, string value, string? colorKey)
    {
        var border = new Border
        {
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(8, 5, 8, 5),
        };
        border.SetResourceReference(FrameworkElement.StyleProperty, "Card");

        var stack = new StackPanel();
        var val = Text(value, "T2", 13, FontWeights.SemiBold);
        if (colorKey != null)
        {
            try { val.SetResourceReference(TextBlock.ForegroundProperty, colorKey); } catch { }
        }
        stack.Children.Add(val);
        stack.Children.Add(Text(label, "T3", 9.5));
        border.Child = stack;
        return border;
    }

    private void RefreshItems(Snapshot s)
    {
        if (_itemsPanel == null) return;
        _itemsPanel.Children.Clear();

        if (s.Items.Count == 0)
        {
            _itemsPanel.Children.Add(Text("（暂无出货）", "T3", 10.5));
            return;
        }

        // 按地图分组：地图名 → 物品行
        foreach (var group in s.Items
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Map) ? "未分类" : x.Map)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var head = Text($"===== {group.Key} =====", "T3", 10.5, margin: new Thickness(0, 6, 0, 2));
            try { head.SetResourceReference(TextBlock.ForegroundProperty, "AppText2"); } catch { }
            _itemsPanel.Children.Add(head);

            foreach (var it in group.OrderByDescending(x => x.Qty).ThenBy(x => x.Name, StringComparer.Ordinal))
            {
                var row = new Grid { Margin = new Thickness(2, 1, 2, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

                var name = Text(it.Name, "T2", 11);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                var qty = Text("×" + it.Qty.ToString("N0"), "T3", 11);
                qty.HorizontalAlignment = HorizontalAlignment.Right;
                try { qty.SetResourceReference(TextBlock.ForegroundProperty, "AppText2"); } catch { }
                Grid.SetColumn(qty, 1);
                row.Children.Add(qty);

                _itemsPanel.Children.Add(row);
            }
        }
    }

    private void RefreshLayers(Snapshot s)
    {
        if (_layersPanel == null) return;
        _layersPanel.Children.Clear();

        if (s.Layers.Count == 0 && s.Customs.Count == 0)
        {
            _layersPanel.Children.Add(Text("（暂无记录）", "T3", 10.5));
            return;
        }

        foreach (var ly in s.Layers.OrderByDescending(x => x.Count))
        {
            var row = new Grid { Margin = new Thickness(2, 1, 2, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var name = Text("层级：" + ly.Name, "T2", 11);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var cnt = Text("×" + ly.Count.ToString("N0"), "T3", 11);
            cnt.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(cnt, 1);
            row.Children.Add(cnt);
            _layersPanel.Children.Add(row);
        }

        if (s.Customs.Count > 0)
        {
            var head = Text("--- 其他记录 ---", "T3", 10.5, margin: new Thickness(0, 6, 0, 2));
            try { head.SetResourceReference(TextBlock.ForegroundProperty, "AppText2"); } catch { }
            _layersPanel.Children.Add(head);

            foreach (var c in s.Customs)
            {
                var row = new Grid { Margin = new Thickness(2, 1, 2, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

                var name = Text(c.Name, "T2", 11);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                var total = Text(c.Total.ToString("N0"), "T3", 11);
                total.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(total, 1);
                row.Children.Add(total);
                _layersPanel.Children.Add(row);
            }
        }
    }

    private void SetStatus(string text)
    {
        _status = text;
        try { _ctx?.Log.Debug(text); } catch { }
    }

    // ---------------- 设置面板 ----------------

    private FrameworkElement BuildSettings()
    {
        var root = new StackPanel();

        root.Children.Add(Text("连接 ACT 挖宝会计的悬浮窗服务（OverlayServer）", "T2", 12, FontWeights.SemiBold));
        root.Children.Add(Text(
            "在 ACT 里打开 FF14TreasureHunterACT 的设置，确认「悬浮窗服务」已开启（overlay_enabled），端口默认 8765。\n"
            + "本机回环访问不需要 token，插件直接用 WebSocket 读数据，不会改动 ACT 项目。",
            "T3", 10.5, margin: new Thickness(0, 4, 0, 0)).Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        // 主机 + 端口
        var hostRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        hostRow.Children.Add(Text("主机", "T3", 10.5, margin: new Thickness(0, 0, 6, 0)));
        var hostBox = new TextBox { Text = WsHost, Width = 140, Height = 26, FontSize = 11 };
        hostBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        hostRow.Children.Add(hostBox);
        hostRow.Children.Add(Text("端口", "T3", 10.5, margin: new Thickness(12, 0, 6, 0)));
        var portBox = new TextBox { Text = WsPort.ToString(), Width = 70, Height = 26, FontSize = 11 };
        portBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        hostRow.Children.Add(portBox);
        root.Children.Add(hostRow);

        // 开关
        var toggleRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var predCheck = new CheckBox
        {
            Content = "鉴定成功/失败通知", IsChecked = NotifyPredictions, FontSize = 11,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        predCheck.SetResourceReference(Control.ForegroundProperty, "AppText3");
        toggleRow.Children.Add(predCheck);

        var itemCheck = new CheckBox
        {
            Content = "出货合并通知", IsChecked = NotifyItems, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        itemCheck.SetResourceReference(Control.ForegroundProperty, "AppText3");
        toggleRow.Children.Add(itemCheck);
        root.Children.Add(toggleRow);

        // 合并秒数
        var mergeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        mergeRow.Children.Add(Text("出货合并等待（秒）", "T3", 10.5, margin: new Thickness(0, 0, 6, 0)));
        var mergeBox = new TextBox { Text = MergeSeconds.ToString(), Width = 70, Height = 26, FontSize = 11 };
        mergeBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        mergeRow.Children.Add(mergeBox);
        mergeRow.Children.Add(Text("重连间隔（秒）", "T3", 10.5, margin: new Thickness(12, 0, 6, 0)));
        var reBox = new TextBox { Text = ReconnectSeconds.ToString(), Width = 70, Height = 26, FontSize = 11 };
        reBox.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        mergeRow.Children.Add(reBox);
        root.Children.Add(mergeRow);

        // 保存 / 重连
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button
        {
            Content = "保存并重连", Height = 28, Padding = new Thickness(12, 0, 12, 0),
        };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        save.Click += (_, _) =>
        {
            try
            {
                SetCfg("host", (hostBox.Text ?? "127.0.0.1").Trim());
                SetCfg("port", Math.Clamp(int.TryParse((portBox.Text ?? "").Trim(), out var p) ? p : 8765, 1, 65535));
                SetCfg("notify_predictions", predCheck.IsChecked == true);
                SetCfg("notify_items", itemCheck.IsChecked == true);
                SetCfg("item_merge_seconds", Math.Clamp(int.TryParse((mergeBox.Text ?? "").Trim(), out var m) ? m : 4, 1, 30));
                SetCfg("reconnect_seconds", Math.Clamp(int.TryParse((reBox.Text ?? "").Trim(), out var r) ? r : 5, 1, 60));
                StartConnection();
                _ctx?.Log.Info("挖宝悬浮窗设置已保存并重新连接");
            }
            catch (Exception ex) { _ctx?.Log.Error("保存挖宝悬浮窗设置失败", ex); }
        };
        btnRow.Children.Add(save);
        root.Children.Add(btnRow);

        root.Children.Add(Text(
            "通知说明：\n"
            + "· 鉴定成功 / 失败会推送一条消息，并在岛内用自绘宝箱图案提示（开箱发光 / 箱子打叉）；\n"
            + "· 获得物品会攒起来合并成一条「本轮出货 · 共 N 件」，明细在悬浮窗里按地图分组查看；\n"
            + "· 想让通知响铃：去 设置页 →「消息提示音」打开「插件来源：FF14 挖宝悬浮窗」。",
            "T3", 10.5, margin: new Thickness(0, 12, 0, 0)).Also(tb => tb.TextWrapping = TextWrapping.Wrap));

        return root;
    }

    // ---------------- UI 小工具 ----------------

    private Button SmallButton(string label, double leftMargin = 0)
    {
        var b = new Button
        {
            Content = label,
            Height = 24,
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 10.5,
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
