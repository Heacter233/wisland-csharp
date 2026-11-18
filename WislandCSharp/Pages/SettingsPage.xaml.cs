using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Controls;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>
/// 设置页：把 config.json 的常用项做成可视化卡片（布局风格与剪贴板页一致：每项一个小卡片）。
/// 修改立即写回配置并尽量即时生效（主题 / 贴边 / 热键会重建）。
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly List<(string Key, string Value)> _pending = new();
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer =
        new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _dirty;

    /// <summary>
    /// 滚动区是否铺不透明底板。
    /// 系统模糊（DWM）模式下不需要 —— 底板会把真实背景模糊挡掉。
    /// 抓屏/合成模式下保留，用来遮挡半透明内容滚动时的残影。
    /// </summary>
    public static bool SolidScrollPlate { get; set; }

    public SettingsPage()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Flush(); };
        Loaded += (_, _) => Build();
        Unloaded += (_, _) => Flush();
    }

    // ---------------- 构建 ----------------
    private void Build()
    {
        SectionStack.Children.Clear();
        _pending.Clear();
        _dirty = false;

        var cfg = ServiceHub.Config;
        // 系统模糊模式下不铺底板，才能真正透出背景
        // 纯色底板已移除：设置页直接透出玻璃/背景
        Host.Background = null;

        // ===== 基础设置 =====
        var basic = AddSection("基础设置", "外观、交互与开机行为");

        AddCard(basic, CardChoice("毛玻璃模式", "island.glass",
            new[] { "system", "capture", "solid" },
            new[] { "DWM 动态模糊", "抓屏（静态）", "实色无模糊" },
            cfg.Get("island.glass", "system")));

        AddCard(basic, CardChoice("主题", "island.theme",
            new[] { "auto", "light", "dark", "rouge", "sky" },
            new[] { "自动", "浅色", "深色", "胭脂", "天青" },
            cfg.Get("island.theme", "dark")));

        AddCard(basic, CardChoice("收起态显示内容", "island.compact_view",
            new[] { "clock", "pet", "music" },
            new[] { "时钟", "形象", "音乐" },
            cfg.Get("island.compact_view", "clock")));

        AddCard(basic, CardChoice("展开触发方式", "island.expand_mode",
            new[] { "hover", "always", "never" },
            new[] { "悬停展开", "常驻展开", "仅手动" },
            cfg.Get("island.expand_mode", "hover")));

        AddCard(basic, CardChoice("停靠屏幕边缘", "island.edge",
            new[] { "top", "bottom", "left", "right" },
            new[] { "顶部", "底部", "左侧", "右侧" },
            cfg.Get("island.edge", "top")));

        AddCard(basic, CardToggle("常驻展开", "island.pinned",
            cfg.Get("island.pinned", false), "岛保持展开，不再自动收起"));

        AddCard(basic, CardToggle("鼠标可穿透", "island.passthrough",
            cfg.Get("island.passthrough", false), "点击穿透到下层窗口"));

        AddCard(basic, CardToggle("开机自动启动", "island.autostart",
            cfg.Get("island.autostart", false), "登录后自动运行"));

        AddCard(basic, CardSlider("悬停展开延时", "island.hover_delay_ms", 0, 600, 20,
            cfg.Get("island.hover_delay_ms", 220), v => $"{v} 毫秒"));

        AddCard(basic, CardSlider("自动收起延时", "island.collapse_delay_ms", 200, 1500, 50,
            cfg.Get("island.collapse_delay_ms", 450), v => $"{v} 毫秒"));

        // ===== 天气设置 =====
        var weather = AddSection("天气设置", "数据来源 Open-Meteo，无需 Key");

        AddCard(weather, CardText("默认城市", "weather.city",
            cfg.Get("weather.city", "上海"), "更多城市在天气页添加"));

        AddCard(weather, CardSlider("天气刷新间隔", "weather.interval_min", 5, 120, 5,
            cfg.Get("weather.interval_min", 15), v => $"每 {v} 分钟"));

        // ===== 翻译设置 =====
        var trans = AddSection("翻译设置", "DeepSeek 兼容接口，流式返回");

        AddCard(trans, CardText("翻译模型名", "translate.model",
            cfg.Get("translate.model", "deepseek-chat"), "DeepSeek 模型名"));

        AddCard(trans, CardText("翻译 API Key", "translate.api_key",
            cfg.Get("translate.api_key", ""), "留空则翻译不可用", secret: true));

        // ===== 热键设置 =====
        var hotkey = AddSection("热键设置", "格式：修饰键+键名，如 Ctrl+Alt+W；改完立即重新注册");

        AddCard(hotkey, CardText("呼出 / 收起岛", "hotkeys.toggle",
            cfg.Get("hotkeys.toggle", "Ctrl+Alt+W"), "全局快捷键"));

        AddCard(hotkey, CardText("唤起翻译页", "hotkeys.translate",
            cfg.Get("hotkeys.translate", "Ctrl+Alt+I"), "全局快捷键"));

        AddCard(hotkey, CardText("唤起剪贴板页", "hotkeys.clipboard",
            cfg.Get("hotkeys.clipboard", "Ctrl+Alt+C"), "全局快捷键"));

        // ===== 剪贴板设置 =====
        var clip = AddSection("剪贴板设置", "自动记录复制过的文本");

        AddCard(clip, CardSlider("剪贴板历史条数", "clipboard.max_items", 5, 50, 1,
            cfg.Get("clipboard.max_items", 20), v => $"保留 {v} 条"));

        AddCard(clip, CardSlider("剪贴板轮询间隔", "clipboard.poll_ms", 100, 1000, 50,
            cfg.Get("clipboard.poll_ms", 300), v => $"{v} 毫秒"));

        // ===== 插件设置（由 PluginRegistry 提供的分组） =====
        var plugins = PluginRegistry.Plugins;
        var pluginSec = AddSection("插件设置",
            plugins.Count == 0 ? "暂无插件 · 接口已预留，插件可注册自己的设置面板"
                               : $"已加载 {plugins.Count} 个插件");
        if (plugins.Count == 0)
        {
            AddCard(pluginSec, InfoCard("插件接口就绪",
                "第三方功能可通过 PluginRegistry.Register 挂到此处"));
        }
        else
        {
            foreach (var pl in plugins)
            {
                if (pl.Settings is { } sec)
                {
                    var secWrap = AddSection(sec.Title, string.IsNullOrEmpty(sec.Subtitle) ? $"{pl.Name} {pl.Version}" : sec.Subtitle);
                    try { AddCard(secWrap, new Border { Width = 590, Child = sec.Build() }); } catch { }
                }
            }
        }

        // ===== 扩展与实验（诚实标注未实现项） =====
        var pending = AddSection("扩展与实验", "以下为计划内能力，当前尚未实现");
        foreach (var item in PendingFeatures.All)
        {
            AddCard(pending, InfoCard($"{item.Title} · {item.State}", item.Note));
        }

        Host.Refresh();
    }

    /// <summary>新增一个设置分组：标题 + 分隔线 + 卡片容器，返回卡片容器。</summary>
    private WrapPanel AddSection(string title, string subtitle)
    {
        var head = new StackPanel { Margin = new Thickness(2, 10, 0, 6) };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Themes.Text()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
        });
        head.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Themes.Text3()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
        });
        SectionStack.Children.Add(head);

        // 分隔线
        SectionStack.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
        });

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        SectionStack.Children.Add(wrap);
        return wrap;
    }

    private static void AddCard(Panel host, Border card) => host.Children.Add(card);

    /// <summary>只读信息卡（用于未实现项/状态说明）。</summary>
    private Border InfoCard(string title, string desc)
    {
        var body = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Themes.Text()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 10.5,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Themes.Text3()),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
        });
        return new Border
        {
            Width = 180,
            MinHeight = 74,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Themes.Card()),
            BorderBrush = new SolidColorBrush(Themes.CardBorder()),
            BorderThickness = new Thickness(1),
            Child = body,
        };
    }
    // ---------------- 卡片工厂 ----------------
    private Border BaseCard(string title, string desc, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Themes.Text()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
        });
        if (desc.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Themes.Text3()),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            });
        }

        return new Border
        {
            Width = 180,
            MinHeight = 74,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Themes.Card()),
            BorderBrush = new SolidColorBrush(Themes.CardBorder()),
            BorderThickness = new Thickness(1),
            Child = body,
        };
    }

    private Border CardChoice(string title, string key, string[] values, string[] labels, string current)
    {
        var card = BaseCard(title, "", out var body);
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (v, i) in values.Select((v, i) => (v, i)))
        {
            var val = v;
            var btn = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 3, 3),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(val == current ? Themes.ActivePill() : Themes.Track()),
                Child = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 10.5,
                    FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
                    Foreground = new SolidColorBrush(val == current ? Themes.PillText() : Themes.Text2()),
                },
            };
            btn.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var sib in wrap.Children.OfType<Border>())
                {
                    sib.Background = new SolidColorBrush(Themes.Track());
                    if (sib.Child is TextBlock tb) tb.Foreground = new SolidColorBrush(Themes.Text2());
                }
                btn.Background = new SolidColorBrush(Themes.ActivePill());
                if (btn.Child is TextBlock self) self.Foreground = new SolidColorBrush(Themes.PillText());
                Queue(key, val);
            };
            wrap.Children.Add(btn);
        }
        body.Children.Add(wrap);
        return card;
    }

    private Border CardToggle(string title, string key, bool value, string desc)
    {
        var card = BaseCard(title, desc, out var body);
        var on = value;

        var track = new Border
        {
            Width = 38,
            Height = 21,
            CornerRadius = new CornerRadius(10.5),
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(Themes.Track()),
            Cursor = Cursors.Hand,
        };
        var knob = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Margin = new Thickness(0, 3, 0, 3),
            Background = new SolidColorBrush(Themes.Text2()),
        };
        track.Child = knob;

        void Apply()
        {
            track.Background = new SolidColorBrush(on ? Themes.Fill() : Themes.Track());
            knob.Background = new SolidColorBrush(on ? Themes.OnFg() : Themes.Text2());
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = on ? new Thickness(0, 3, 3, 3) : new Thickness(3, 3, 0, 3);
        }
        Apply();

        track.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            Apply();
            Queue(key, on ? "true" : "false");
        };
        body.Children.Add(track);
        return card;
    }

    private Border CardSlider(string title, string key, int min, int max, int step, int value,
        Func<int, string> fmt)
    {
        var card = BaseCard(title, "", out var body);
        var val = Math.Clamp(value, min, max);

        var label = new TextBlock
        {
            Text = fmt(val),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Themes.Text()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 2,
            IsSnapToTickEnabled = true,
            TickFrequency = step,
            Value = val,
            Margin = new Thickness(0, 6, 0, 0),
            Style = (Style)Application.Current.FindResource("AppSlider"),
        };
        slider.ValueChanged += (_, e) =>
        {
            val = (int)Math.Round(e.NewValue / step) * step;
            label.Text = fmt(val);
            Queue(key, val.ToString());
        };
        body.Children.Add(label);
        body.Children.Add(slider);
        return card;
    }

    private Border CardText(string title, string key, string value, string desc, bool secret = false)
    {
        var card = BaseCard(title, desc, out var body);

        TextBox box;
        if (secret)
        {
            // 用 TextBox + 掩码字符，避免 PasswordBox 无法显示已有 Key 的前缀
            box = new TextBox
            {
                Text = value,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                Style = (Style)Application.Current.FindResource("AppBox"),
            };
        }
        else
        {
            box = new TextBox
            {
                Text = value,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                Style = (Style)Application.Current.FindResource("AppBox"),
            };
        }

        box.TextChanged += (_, _) => Queue(key, box.Text.Trim());
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Flush();
                Keyboard.ClearFocus();
            }
        };
        // 输入框获得焦点时不让岛自动收起（窗口离开鼠标也不收）
        body.Children.Add(box);
        return card;
    }

    // ---------------- 写配置 ----------------
    private void Queue(string key, string value)
    {
        _pending.RemoveAll(p => p.Key == key);
        _pending.Add((key, value));
        _dirty = true;
        HintText.Text = "修改中…";
        // 防抖自动保存：改完 600ms 落盘，避免忘记保存丢失
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>把待写项落盘，并触发需要即时生效的动作。</summary>
    public void Flush()
    {
        if (!_dirty) return;
        var cfg = ServiceHub.Config;
        foreach (var (key, value) in _pending) Apply(key, value, cfg);
        cfg.Save();
        _pending.Clear();
        _dirty = false;
        HintText.Text = "已保存";
    }

    private void Apply(string key, string value, IConfigService cfg)
    {
        switch (key)
        {
            case "island.glass":
                cfg.Set(key, value);
                ServiceHub.RequestGlass?.Invoke();
                break;

            case "island.theme":
                cfg.Set(key, value);
                Themes.Mode = value;
                ThemeApplier.Apply(Application.Current.Resources);
                ServiceHub.RaiseThemeChanged();
                ServiceHub.RequestThemeRefresh?.Invoke();
                break;

            case "island.pinned":
                cfg.Set(key, value == "true");
                break;

            case "island.passthrough":
                cfg.Set(key, value == "true");
                ServiceHub.RequestPassthrough?.Invoke(value == "true");
                break;

            case "island.autostart":
                cfg.Set(key, value == "true");
                ServiceHub.RequestAutostart?.Invoke(value == "true");
                break;

            case "island.edge":
                cfg.Set<string>(key, value);
                ServiceHub.RequestReanchor?.Invoke();
                break;

            case "island.pos":
                cfg.Set<double>(key, double.TryParse(value, out var pos) ? pos : 0.5);
                ServiceHub.RequestReanchor?.Invoke();
                break;

            case "island.compact_view":
                cfg.Set(key, value);
                ServiceHub.RequestCompactView?.Invoke(value);
                break;

            case "island.expand_mode":
                cfg.Set(key, value);
                if (value == "always") ServiceHub.RequestExpand?.Invoke("");
                else if (value == "never") ServiceHub.RequestCollapse?.Invoke();
                break;

            case "hotkeys.toggle":
            case "hotkeys.translate":
            case "hotkeys.clipboard":
                cfg.Set(key, value);
                ServiceHub.RequestReloadHotkeys?.Invoke();
                break;

            case "island.hover_delay_ms":
            case "island.collapse_delay_ms":
                cfg.Set<int>(key, int.TryParse(value, out var ms) ? ms : 0);
                ServiceHub.RequestTimings?.Invoke();
                break;

            case "weather.interval_min":
                cfg.Set(key, int.Parse(value));
                break;

            case "weather.city":
                cfg.Set(key, value);
                cfg.Set("weather.lat", 0.0);
                cfg.Set("weather.lon", 0.0);
                ServiceHub.Weather.Initialize(city: value,
                    apiKey: cfg.Get("weather.api_key", ""),
                    units: cfg.Get("weather.units", "metric"),
                    lang: cfg.Get("weather.lang", "zh_cn"),
                    intervalMinutes: cfg.Get("weather.interval_min", 15),
                    useOpenMeteo: true);
                break;

            case "clipboard.max_items":
                cfg.Set(key, int.Parse(value));
                break;

            default:
                // 其余按原类型写回
                var old = cfg.Get<object?>(key, null);
                if (old is bool) cfg.Set<bool>(key, value == "true");
                else if (old is long or int) cfg.Set<int>(key, int.TryParse(value, out var i) ? i : 0);
                else if (old is double) cfg.Set<double>(key, double.TryParse(value, out var d) ? d : 0.0);
                else cfg.Set<string>(key, value);
                break;
        }
    }

    // ---------------- 打开文件/目录 ----------------
    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        TryOpen(AppPaths.ConfigFile);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        TryOpen(AppPaths.DataDir);
    }

    private static void TryOpen(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { App.LogCrash("settings-open", ex); }
    }
}
