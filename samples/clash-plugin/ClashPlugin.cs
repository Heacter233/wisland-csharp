using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Plugins;
using Wisland.Services;

namespace ClashPlugin;

/// <summary>
/// Clash 控制插件：通过 Clash 外部控制器（External Controller）切换模式与节点。
///
/// 支持的接口（mihomo / Clash 通用）：
///   GET  /version                          → {version}
///   GET  /configs                          → {mode: rule|global|direct}
///   PATCH/configs  {"mode":"global"}       → 切换模式
///   GET  /proxies                          → {proxies:{ "组名": {type, now, all[]} }}
///   PUT  /proxies/{组名}  {"name":"节点"}   → 切换节点
///   GET  /proxies/{节点}/delay?timeout=3000&url=http://www.gstatic.com/generate_204
///
/// 注意：Clash Verge 默认只在 127.0.0.1:9097 监听，也可能只开命名管道
/// （\\.\pipe\verge-mihomo）。本插件用 HTTP，端口/密钥需与客户端配置一致。
/// </summary>
public sealed class ClashPlugin : IWislandPlugin
{
    private IPluginContext? _ctx;
    private IDisposable? _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private string _status = "尚未连接";
    private string _version = "";
    private string _mode = "";
    private bool _busy;

    private readonly List<Group> _groups = new();
    private StackPanel? _modeRow;
    private StackPanel? _groupHost;
    private TextBlock? _statusText;

    private sealed class Group
    {
        public string Name = "";
        public string Now = "";
        public List<string> All = new();
    }

    // ---------------- 配置 ----------------

    private string Host => _ctx?.Config.Get("host", "127.0.0.1") ?? "127.0.0.1";
    private int Port => _ctx?.Config.Get("port", 9097) ?? 9097;
    private string Secret => _ctx?.Config.Get("secret", "") ?? "";
    private string Base => $"http://{Host}:{Port}";

    /// <summary>连接方式：auto（先 HTTP 再命名管道）/ http / pipe。</summary>
    private string Transport => (_ctx?.Config.Get("transport", "auto") ?? "auto").ToLowerInvariant();

    /// <summary>命名管道名（不含 \\.\pipe\ 前缀）。</summary>
    private string PipeName
    {
        get
        {
            var raw = _ctx?.Config.Get("pipe", "verge-mihomo") ?? "verge-mihomo";
            var i = raw.LastIndexOf('\\');
            return i >= 0 ? raw[(i + 1)..] : raw;
        }
    }

    /// <summary>上一次成功的通道（auto 模式下记住，避免每次都试两遍）。</summary>
    private string _working = "";

    // ---------------- 生命周期 ----------------

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;
        context.RegisterSettingsSection("Clash 控制", "外部控制器地址与密钥", BuildSettings);
        context.RegisterPage(new PluginPageDescriptor
        {
            Key = "clash",
            Title = "Clash 控制",
            Glyph = "\uE774",           // 地球/网络类字形
            Height = 340,
            Build = BuildPage,
        });

        var interval = Math.Max(5, context.Config.Get("refresh_sec", 15));
        _timer = context.RegisterTimer(TimeSpan.FromSeconds(interval), () => _ = RefreshAsync());
        _ = RefreshAsync();
    }

    public void OnUnload()
    {
        _timer?.Dispose();
        _timer = null;
        _ctx = null;
    }

    // ---------------- HTTP ----------------

    private HttpRequestMessage Req(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Base + path);
        if (!string.IsNullOrEmpty(Secret))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        if (json != null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var transport = Transport;

        // 指定或已确定可用通道时走单一路径
        if (transport == "pipe") { var rp = await SendViaPipeAsync(method, path, json); _working = "pipe"; return rp; }
        if (transport == "http") { var rh = await SendViaHttpAsync(method, path, json); _working = "http"; return rh; }
        if (_working == "pipe") return await SendViaPipeAsync(method, path, json);
        if (_working == "http") return await SendViaHttpAsync(method, path, json);

        // auto：先试 HTTP（最常见），失败再试命名管道，并记住结果
        try
        {
            var r = await SendViaHttpAsync(method, path, json);
            _working = "http";
            return r;
        }
        catch
        {
            var r = await SendViaPipeAsync(method, path, json);
            _working = "pipe";
            _ctx?.Log.Info($"改用命名管道 \\\\.\\pipe\\{PipeName}");
            return r;
        }
    }

    private async Task<string> SendViaHttpAsync(HttpMethod method, string path, string? json)
    {
        using var req = Req(method, path, json);
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return body;
    }

    private async Task<string> SendViaPipeAsync(HttpMethod method, string path, string? json)
    {
        var e = await PipeHttp.SendAsync(PipeName, method.Method, path, Secret, json, TimeSpan.FromSeconds(6));
        if (!e.Ok)
            throw new HttpRequestException($"Pipe HTTP {e.StatusCode} {e.Reason}".Trim());
        return e.Body;
    }

    // ---------------- 刷新 ----------------

    private async Task RefreshAsync()
    {
        if (_busy || _ctx == null) return;
        _busy = true;
        try
        {
            // 版本（顺带当作连通性探测）
            try
            {
                var vj = await SendAsync(HttpMethod.Get, "/version");
                using var d = JsonDocument.Parse(vj);
                _version = d.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            }
            catch
            {
                _version = "";
            }

            var cfg = await SendAsync(HttpMethod.Get, "/configs");
            using (var d = JsonDocument.Parse(cfg))
            {
                _mode = d.RootElement.TryGetProperty("mode", out var m) ? (m.GetString() ?? "") : "";
            }

            var proxies = await SendAsync(HttpMethod.Get, "/proxies");
            _groups.Clear();
            using (var d = JsonDocument.Parse(proxies))
            {
                if (d.RootElement.TryGetProperty("proxies", out var root) &&
                    root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        if (!p.Value.TryGetProperty("type", out var type)) continue;
                        var t = type.GetString() ?? "";
                        if (!t.Equals("Selector", StringComparison.OrdinalIgnoreCase)) continue;

                        var g = new Group
                        {
                            Name = p.Name,
                            Now = p.Value.TryGetProperty("now", out var now) ? now.GetString() ?? "" : "",
                        };
                        if (p.Value.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var n in all.EnumerateArray())
                            {
                                var s = n.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) g.All.Add(s!);
                            }
                        }
                        _groups.Add(g);
                    }
                }
            }

            // 常用组排前面
            _groups.Sort((a, b) => Rank(a.Name).CompareTo(Rank(b.Name)));

            _status = (_version.Length > 0 ? $"已连接 Clash {_version}" : "已连接")
                    + $" · {DateTime.Now:HH:mm:ss}"
                    + (_groups.Count > 0 ? $" · {_groups.Count} 个策略组" : "")
                    + (_working == "pipe" ? " · 命名管道" : _working == "http" ? " · HTTP" : "");
        }
        catch (Exception ex)
        {
            _status = "连接失败：" + Short(ex)
                    + $"\n已尝试 HTTP({Base}) 与命名管道(\\\\pipe\\{PipeName})。"
                    + "请确认 Clash 已开启外部控制器、端口/密钥/管道名正确。";
            _ctx.Log.Warn($"Clash 连接失败 [transport={Transport} working={_working} pipe={PipeName}] "
                        + $"{ex.GetType().Name}: {ex.GetBaseException().Message}");
        }
        finally
        {
            _busy = false;
            UpdateUi();
        }
    }

    private static int Rank(string name)
    {
        if (name.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)) return 90;
        if (name.Contains("选择", StringComparison.Ordinal) || name.Contains("Select", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("PROXY", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("自动", StringComparison.Ordinal) || name.Contains("Auto", StringComparison.OrdinalIgnoreCase)) return 2;
        return 10;
    }

    private static string Short(Exception ex)
    {
        var e = ex.GetBaseException();
        if (e is HttpRequestException && e.Message.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return "端口未监听";
        if (e is TaskCanceledException) return "超时";
        var m = e.Message;
        return m.Length > 70 ? m[..70] : m;
    }

    private async Task SetModeAsync(string mode)
    {
        try
        {
            await SendAsync(HttpMethod.Patch, "/configs", $"{{\"mode\":\"{mode}\"}}");
            _mode = mode;
            _status = $"已切换到 {ModeName(mode)} · {DateTime.Now:HH:mm:ss}";
            _ctx?.Log.Info("切换 Clash 模式：" + mode);
        }
        catch (Exception ex)
        {
            _status = "切换模式失败：" + Short(ex);
        }
        UpdateUi();
    }

    private async Task SelectNodeAsync(string group, string node)
    {
        try
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = node });
            await SendAsync(HttpMethod.Put, "/proxies/" + Uri.EscapeDataString(group), body);
            var g = _groups.Find(x => x.Name == group);
            if (g != null) g.Now = node;
            _status = $"{group} → {node}";
            _ctx?.Log.Info($"切换节点：{group} → {node}");
        }
        catch (Exception ex)
        {
            _status = "切换节点失败：" + Short(ex);
        }
        UpdateUi();
    }

    private static string ModeName(string mode) => mode switch
    {
        "rule" => "规则",
        "global" => "全局",
        "direct" => "直连",
        _ => mode,
    };

    // ---------------- 页面 ----------------

    private FrameworkElement BuildPage()
    {
        var root = new StackPanel { Margin = new Thickness(4, 2, 4, 0) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text("Clash 控制", "T1", 15, FontWeights.SemiBold));

        var refresh = new Button
        {
            Content = "刷新",
            Height = 24,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
        };
        refresh.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        refresh.Click += (_, _) => _ = RefreshAsync();
        head.Children.Add(refresh);
        root.Children.Add(head);

        _statusText = Text(_status, "T3", 11.5);
        _statusText.Margin = new Thickness(0, 4, 0, 8);
        _statusText.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_statusText);

        _modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(_modeRow);

        _groupHost = new StackPanel();
        root.Children.Add(_groupHost);

        UpdateUi();
        return root;
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

        if (_modeRow != null)
        {
            _modeRow.Children.Clear();
            foreach (var mode in new[] { "rule", "global", "direct" })
            {
                var active = string.Equals(_mode, mode, StringComparison.OrdinalIgnoreCase);
                var b = new Button
                {
                    Content = ModeName(mode),
                    Height = 26,
                    Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = 12,
                    FocusVisualStyle = null,
                    Cursor = Cursors.Hand,
                };
                b.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
                if (active)
                {
                    b.SetResourceReference(Control.BackgroundProperty, "AppPill");
                    b.SetResourceReference(Control.ForegroundProperty, "AppPillFg");
                }
                var captured = mode;
                b.Click += (_, _) => _ = SetModeAsync(captured);
                _modeRow.Children.Add(b);
            }
        }

        if (_groupHost == null) return;
        _groupHost.Children.Clear();
        if (_groups.Count == 0) return;

        foreach (var g in _groups.Take(4))
        {
            var card = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(10),
                Background = Brush("AppCard"),
            };

            var sp = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(Text(g.Name, "T2", 12.5, FontWeights.SemiBold));
            var now = Text("  " + (g.Now.Length > 0 ? g.Now : "-"), "T3", 11.5);
            titleRow.Children.Add(now);
            sp.Children.Add(titleRow);

            // 节点标签（横向滚动，避免撑爆高度）
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var scroll = new ScrollViewer
            {
                Content = chips,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 30,
            };
            scroll.PreviewMouseWheel += (_, e) =>
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

            foreach (var node in g.All.Take(12))
            {
                var selected = string.Equals(node, g.Now, StringComparison.Ordinal);
                var chip = new Button
                {
                    Content = node.Length > 14 ? node[..13] + "…" : node,
                    Height = 22,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(9, 0, 9, 0),
                    FontSize = 11,
                    FocusVisualStyle = null,
                    Cursor = Cursors.Hand,
                };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
                if (selected)
                {
                    chip.SetResourceReference(Control.BackgroundProperty, "AppPill");
                    chip.SetResourceReference(Control.ForegroundProperty, "AppPillFg");
                }
                var groupName = g.Name;
                var nodeName = node;
                chip.Click += (_, _) => _ = SelectNodeAsync(groupName, nodeName);
                chips.Children.Add(chip);
            }

            sp.Children.Add(scroll);
            card.Child = sp;
            _groupHost.Children.Add(card);
        }

        // 页面视觉树变了：请求宿主重绘
        _ctx?.RequestRepaint();
    }

    // ---------------- 设置面板 ----------------

    private FrameworkElement BuildSettings()
    {
        var root = new StackPanel();

        root.Children.Add(Text("外部控制器地址", "T3", 11.5));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        var host = new TextBox { Text = Host, Width = 140, Height = 32 };
        host.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        row.Children.Add(host);

        row.Children.Add(Text(":", "T3", 13));
        var port = new TextBox { Text = Port.ToString(), Width = 70, Height = 32, Margin = new Thickness(2, 0, 0, 0) };
        port.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        row.Children.Add(port);
        root.Children.Add(row);

        root.Children.Add(Text("密钥（secret，可留空）", "T3", 11.5));
        var secret = new TextBox { Text = Secret, Height = 32, Margin = new Thickness(0, 6, 0, 0) };
        secret.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        root.Children.Add(secret);

        // 连接方式：auto / http / pipe
        root.Children.Add(Text("连接方式（Clash Verge 可能只开命名管道）", "T3", 11.5));
        var transportRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var current = Transport;
        foreach (var (value, label) in new[] { ("auto", "自动"), ("http", "HTTP"), ("pipe", "命名管道") })
        {
            var active = current == value;
            var b = new Button
            {
                Content = label,
                Height = 26,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12,
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,
            };
            b.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
            if (active)
            {
                b.SetResourceReference(Control.BackgroundProperty, "AppPill");
                b.SetResourceReference(Control.ForegroundProperty, "AppPillFg");
            }
            var captured = value;
            b.Click += (_, _) =>
            {
                _ctx?.Config.Set("transport", captured);
                _working = "";
                _ = RefreshAsync();
            };
            transportRow.Children.Add(b);
        }
        root.Children.Add(transportRow);

        root.Children.Add(Text("管道名（默认 verge-mihomo）", "T3", 11.5));
        var pipe = new TextBox { Text = PipeName, Height = 32, Margin = new Thickness(0, 6, 0, 0) };
        pipe.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
        root.Children.Add(pipe);

        var save = new Button
        {
            Content = "保存并重连",
            Height = 28,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 0, 12, 0),
        };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        save.Click += (_, _) =>
        {
            _ctx?.Config.Set("host", host.Text.Trim());
            if (int.TryParse(port.Text.Trim(), out var p)) _ctx?.Config.Set("port", p);
            _ctx?.Config.Set("secret", secret.Text.Trim());
            _ctx?.Config.Set("pipe", pipe.Text.Trim());
            _working = "";
            _ctx?.Log.Info($"Clash 设置已保存：{Base} / pipe {PipeName}");
            _ = RefreshAsync();
        };
        root.Children.Add(save);

        root.Children.Add(Text("提示：Clash Verge 默认控制器在 127.0.0.1:9097，密钥见客户端设置；"
                             + "若只启用命名管道（verge-mihomo）则本插件无法连接。", "T3", 11));
        return root;
    }

    // ---------------- UI 小工具 ----------------

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
}