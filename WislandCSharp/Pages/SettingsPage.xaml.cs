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
using Wisland.Plugins;
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
    /// <summary>插件热重载后重建界面（插件分组会随之更新）。</summary>
    public void Rebuild()
    {
        if (!IsLoaded) return;
        try { Build(); } catch { }
    }

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

        AddCard(basic, CardToggle("系统通知进消息页", "island.notify_to_inbox",
            cfg.Get("island.notify_to_inbox", true),
            "把 QQ / 微信 / 邮件的系统通知也写入消息页，磁贴与折叠、置顶统一生效"));

        AddCard(basic, CardChoice("新消息提示方式", "island.notify_style",
            new[] { "popup", "ticker" },
            new[] { "弹出（加高逐条）", "滚动（加宽横向）" },
            cfg.Get("island.notify_style", "popup")));

        AddCard(basic, CardSlider("提示停留时长", "island.notify_duration_ms", 1200, 8000, 500,
            cfg.Get("island.notify_duration_ms", 4000), v => $"{v / 1000.0:0.0} 秒"));

        AddCard(basic, CardToggle("允许插件自定义通知动画", "island.notify_plugin_anim",
            cfg.Get("island.notify_plugin_anim", true),
            "关闭后忽略插件提供的图片/绘制动画，只用默认文字提示"));

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

        // ===== 消息提示音 =====
        var snd = AddSection("消息提示音", "默认关闭；内置 QQ / 微信 / 邮件与插件来源各自独立开关");

        AddCard(snd, CardToggle("开启消息提示音", "notify.sound.enabled",
            cfg.Get("notify.sound.enabled", false),
            "总开关。关闭时任何来源都不会发声"));

        AddCard(snd, CardChoice("提示音类型", "notify.sound.kind",
            new[] { "builtin", "asterisk", "beep", "exclamation", "question", "hand" },
            new[] { "内置提示音", "系统默认", "蜂鸣", "感叹", "疑问", "警告" },
            cfg.Get("notify.sound.kind", "builtin")));

        AddCard(snd, CardText("自定义提示音（.wav）", "notify.sound.file",
            cfg.Get("notify.sound.file", ""),
            "留空使用系统提示音；填绝对路径则播放该 wav"));

        AddCard(snd, CardSlider("提示音冷却", "notify.sound.cooldown_ms", 500, 15000, 500,
            cfg.Get("notify.sound.cooldown_ms", 3000), v => $"{v / 1000.0:0.0} 秒"));

        AddCard(snd, BuildSoundPreviewCard());

        // 按来源开关：内置三个 + 声明了 notify.sound 的插件
        foreach (var (source, name, builtIn) in ServiceHub.NotifySound.Sources())
        {
            var key = "notify.sound.source." + source;
            AddCard(snd, CardToggle(
                builtIn ? $"来源：{name}" : $"插件来源：{name}",
                key,
                cfg.Get(key, false),
                builtIn
                    ? "该来源有新消息时播放提示音"
                    : $"插件 {source} 已声明 notify.sound；打开后它的消息会发声"));
        }
        // ===== 天气设置 =====
        var weather = AddSection("天气设置", "数据来源 Open-Meteo，无需 Key");

        AddCard(weather, CardText("默认城市", "weather.city",
            cfg.Get("weather.city", "上海"), "更多城市在天气页添加"));

        AddCard(weather, CardSlider("天气刷新间隔", "weather.interval_min", 5, 120, 5,
            cfg.Get("weather.interval_min", 15), v => $"每 {v} 分钟"));

        // ===== 翻译设置 =====
        var trans = AddSection("翻译设置",
            "多引擎：Index-Translate（免 Key）/ 智谱 GLM-4-Flash / DeepSeek / 免密钥网页接口；主引擎失败按回退顺序自动切换");

        AddCard(trans, CardChoice("翻译引擎", "translate.engine",
            new[] { "index", "zhipu", "deepseek", "google", "bing" },
            new[] { "Index-Translate（免Key）", "智谱 GLM-4-Flash", "DeepSeek", "Google（免Key）", "Bing（免Key）" },
            cfg.Get("translate.engine", "index")));

        AddCard(trans, CardText("回退顺序", "translate.fallback",
            cfg.Get("translate.fallback", "google"),
            "主引擎失败时按顺序尝试，逗号分隔：index,zhipu,deepseek,google,bing"));

        AddCard(trans, CardText("Index 模型", "translate.index_model",
            cfg.Get("translate.index_model", "Index-Translate-35B-A3B"),
            "B站免费 API 可选：Index-Translate-35B-A3B / Index-Translate-9B / Index-Translate-2B"));

        AddCard(trans, BuildZhipuGuideCard());

        AddCard(trans, CardText("智谱 API Key", "translate.zhipu_api_key",
            cfg.Get("translate.zhipu_api_key", ""),
            "手机号注册智谱开放平台即可免费使用 GLM-4-Flash", secret: true));

        AddCard(trans, CardText("智谱模型名", "translate.zhipu_model",
            cfg.Get("translate.zhipu_model", "glm-4-flash"),
            "免费模型：glm-4-flash / glm-4.7-flash"));

        AddCard(trans, CardText("DeepSeek API Key", "translate.api_key",
            cfg.Get("translate.api_key", ""), "留空则跳过 DeepSeek 引擎", secret: true));

        AddCard(trans, CardText("DeepSeek 模型名", "translate.model",
            cfg.Get("translate.model", "deepseek-flash"), "DeepSeek 模型名"));

        AddCard(trans, BuildTranslateTestCard());

        // ===== 实时字幕 =====
        // 进程内管线：音频捕获（回环/麦克风）→ VAD 切句 → 离线 Whisper → 翻译
        var liveSec = AddSection("实时字幕", "离线识别（Whisper），首次使用会下载模型");

        AddCard(liveSec, CardChoice("音频来源", "live.audio.source",
            new[] { "loopback", "mic" },
            new[] { "系统音频", "麦克风" },
            cfg.Get("live.audio.source", "loopback")));

        AddCard(liveSec, CardChoice("识别语言", "live.asr.language",
            new[] { "auto", "ja", "en", "zh" },
            new[] { "自动", "日语", "英语", "中文" },
            cfg.Get("live.asr.language", "auto")));

        AddCard(liveSec, CardChoice("识别引擎", "live.asr.engine",
            new[] { "auto", "sensevoice", "whisper" },
            new[] { "自动（中文/英文→SenseVoice，日语→Whisper）", "SenseVoice（中文/英文快）", "Whisper（日语推荐）" },
            cfg.Get("live.asr.engine", "auto")));

        AddCard(liveSec, CardToggle("清理 anime-whisper 句首 S", "live.asr.anime_strip_s",
            cfg.Get("live.asr.anime_strip_s", true),
            "anime-whisper 训练数据带说话人标记，句首常多出一个 S（如「S魔物である…」）；开启后自动去掉，不影响 Sランク 等合法词。重启字幕生效"));

        AddCard(liveSec, CardChoice("识别模型（Whisper）", "live.asr.model",
            new[] { "auto", "tiny", "base", "small", "turbo", "anime" },
            new[] { "自动", "tiny 75MB", "base 142MB", "small 466MB",
                    "turbo 547MB（多语·需GPU）", "anime 1.4GB（仅日语·需GPU）" },
            cfg.Get("live.asr.model", "auto")));

        AddCard(liveSec, CardChoice("GPU 加速（Whisper）", "live.asr.gpu",
            new[] { "auto", "off", "vulkan", "cuda" },
            new[] { "自动（Vulkan 优先）", "关闭（只用 CPU）", "Vulkan（AMD/Intel/NVIDIA）", "CUDA（需自备运行库）" },
            cfg.Get("live.asr.gpu", "auto")));

        AddCard(liveSec, CardText("识别线程数", "live.asr.threads",
            cfg.Get("live.asr.threads", 0).ToString(),
            "0 = 自动（核心数-1）；SenseVoice 与 Whisper 共用，重启字幕生效"));

        AddCard(liveSec, BuildModelDownloadCard());

        AddCard(liveSec, CardToggle("字幕翻译", "live.translate.enabled",
            cfg.Get("live.translate.enabled", true),
            "关闭后只显示识别原文；翻译优先用上面的 DeepSeek Key，未填则用免费接口兜底"));

        AddCard(liveSec, CardChoice("译文语言", "live.translate.target",
            new[] { "zh-CN", "zh-TW", "en" },
            new[] { "简体中文", "繁体中文", "英语" },
            cfg.Get("live.translate.target", "zh-CN")));

        AddCard(liveSec, CardChoice("人声处理", "live.enhance.mode",
            new[] { "off", "dpdfnet", "gtcrn", "voxlock" },
            new[] { "关闭（原始音频）", "DPDFNet2 降噪（推荐）", "GTCRN 轻量降噪", "VoxLock 目标说话人（单人）" },
            cfg.Get("live.enhance.mode", "dpdfnet")));

        AddCard(liveSec, BuildEnhanceGuideCard());

        AddCard(liveSec, CardText("音色样本（VoxLock 可选）", "live.enhance.voice_sample",
            cfg.Get("live.enhance.voice_sample", ""),
            "留空=VoxLock 自动锁定第一个说话人；填 3 秒以上语音 wav 可指定要保留的人声"));

        AddCard(liveSec, CardText("人声处理线程数", "live.enhance.threads",
            cfg.Get("live.enhance.threads", 2).ToString(),
            "DPDFNet/GTCRN 建议 2；VoxLock 可调到 4；重启字幕生效"));

        AddCard(liveSec, CardToggle("增强后电平归一化", "live.enhance.normalize",
            cfg.Get("live.enhance.normalize", true),
            "降噪会压低电平（实测 -8~18dB），归一化后 VAD 才不会漏句"));

        AddCard(liveSec, CardToggle("自动增益（AGC）", "live.capture.agc",
            cfg.Get("live.capture.agc", true),
            "人声处理链路下默认关闭（不需要 AGC）；仅在「人声处理=关闭」且音频很轻时打开。改动在重启实时字幕后生效"));

        AddCard(liveSec, CardText("手动增益", "live.capture.gain",
            cfg.Get("live.capture.gain", 1.0).ToString("0.###"),
            "0.1~16，1.0 为原始电平；开了 AGC 一般不用改（与 AGC 叠加）"));

        AddCard(liveSec, CardToggle("自适应语音阈值", "live.vad.adaptive",
            cfg.Get("live.vad.adaptive", true),
            "按环境噪声底动态调整触发阈值，安静环境不会漏句；关掉则只用下面的固定阈值"));

        AddCard(liveSec, CardText("语音阈值", "live.vad.threshold",
            cfg.Get("live.vad.threshold", 0.006).ToString("0.####"),
            "0.003~0.015，越大越难触发；自适应开启时作为下限"));

        AddCard(liveSec, CardSlider("静音切句阈值", "live.vad.silence_ms", 150, 1500, 50,
            cfg.Get("live.vad.silence_ms", 450),
            v => v >= 1000 ? $"{v / 1000.0:0.##} 秒" : $"{v} ms"));

        AddCard(liveSec, CardSlider("最长句时长", "live.vad.max_segment_ms", 2000, 20000, 500,
            cfg.Get("live.vad.max_segment_ms", 12000),
            v => $"{v / 1000.0:0.#} 秒"));

        AddCard(liveSec, BuildVadTuningGuideCard());

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

        // ===== 插件设置（由 PluginLoader / PluginRegistry 提供的分组） =====
        var plugins = PluginRegistry.Plugins;
        var loaded = plugins.Count(p => p.IsLoaded);
        var pluginSec = AddSection("插件设置",
            plugins.Count == 0 ? "暂无插件 · 把插件解压到 plugins\\<id>\\ 即可"
                               : $"已加载 {loaded} / 共 {plugins.Count} 个");

        // 顶部：插件目录 + 说明
        var dirCard = new Border
        {
            Style = (Style)FindResource("Card"),
            Width = 590,
            Child = BuildPluginHint(plugins.Count),
        };
        AddCard(pluginSec, dirCard);

        // 每个插件一张卡：名字/版本/状态/权限/错误/设置入口
        // 卡片之间插一条分隔线，避免两个插件的说明与权限贴在一起难以分辨
        for (var i = 0; i < plugins.Count; i++)
        {
            if (i > 0) AddCard(pluginSec, PluginSeparator());
            AddCard(pluginSec, BuildPluginCard(plugins[i]));
        }

        // ===== 扩展与实验（诚实标注未实现项） =====
        var pending = AddSection("扩展与实验", "以下为计划内能力，当前尚未实现");
        foreach (var item in PendingFeatures.All)
        {
            AddCard(pending, InfoCard($"{item.Title} · {item.State}", item.Note));
        }

        Host.Refresh();
    }

    /// <summary>插件分组顶部：说明 + 打开插件目录。</summary>
    private Border BuildPluginHint(int total)
    {
        var sp = new StackPanel();

        sp.Children.Add(new TextBlock
        {
            Text = total == 0
                ? "把插件解压到程序目录下的 plugins\\<id>\\（内含 plugin.json），会**自动加载**，也可用下面按钮手动重载。"
                : "插件从 plugins\\<id>\\ 加载；改动 dll / plugin.json 会自动重载，也可用下面按钮手动重载。开发规范见 docs/plugins.md。",
            Style = (Style)FindResource("T3"),
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var open = new Button
        {
            Content = "打开插件目录",
            Style = (Style)FindResource("PillBtn"),
            Height = 26,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
        };
        open.Click += (_, _) =>
        {
            try
            {
                var dir = Path.Combine(AppPaths.BaseDir, "plugins");
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        };
        row.Children.Add(open);

        var openData = new Button
        {
            Content = "插件数据目录",
            Style = (Style)FindResource("PillBtn"),
            Height = 26,
            FontSize = 12,
        };
        openData.Click += (_, _) =>
        {
            try
            {
                var dir = Path.Combine(ServiceHub.Config.DataDir, "plugins");
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        };
        row.Children.Add(openData);

        // 重载全部插件：卸载后重新扫描 plugins/（新增/移除/改动都会生效）
        var reloadAll = new Button
        {
            Content = "重新加载全部插件",
            Style = (Style)FindResource("PillBtn"),
            Height = 26,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
        };
        reloadAll.Click += (_, _) =>
        {
            reloadAll.IsEnabled = false;
            reloadAll.Content = "重载中…";
            try
            {
                ServiceHub.RequestReloadPlugins?.Invoke(null);
            }
            finally
            {
                // 重载会重建整个设置页，这个按钮随之被替换，这里只是兜底恢复
                reloadAll.IsEnabled = true;
                reloadAll.Content = "重新加载全部插件";
            }
        };
        row.Children.Add(reloadAll);

        sp.Children.Add(row);

        return new Border { Style = (Style)FindResource("Card"), Width = 590, Child = sp };
    }

    /// <summary>插件卡片之间的分隔线（两个插件的说明/权限贴在一起时不好读）。</summary>
    private Border PluginSeparator() => new()
    {
        Height = 1,
        Width = 560,
        Margin = new Thickness(0, 2, 0, 12),
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
    };

    /// <summary>单个插件卡片：名称/版本/状态/权限/错误 + 设置入口。</summary>
    private Border BuildPluginCard(PluginInfo pl)
    {
        var sp = new StackPanel();

        // 标题行：名字 + 版本 + 状态徽标
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = pl.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Themes.Text()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = "  v" + pl.Version,
            FontSize = 11,
            Foreground = new SolidColorBrush(Themes.Text3()),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(7, 1, 7, 2),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(pl.State switch
            {
                PluginState.Loaded => Themes.OnColor(),
                PluginState.Disabled => Themes.Track(),
                _ => Color.FromArgb(48, 232, 84, 78),
            }),
            Child = new TextBlock
            {
                Text = pl.State switch
                {
                    PluginState.Loaded => "已加载",
                    PluginState.Disabled => "已禁用",
                    _ => "加载失败",
                },
                FontSize = 10.5,
                Foreground = new SolidColorBrush(pl.State == PluginState.Loaded ? Themes.OnFg() : Themes.Text()),
            },
        });
        sp.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(pl.Description))
        {
            // 说明 + 该插件的「重载」按钮（单独重载这一个，不影响其他插件）
            var descRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            descRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            descRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var desc = new TextBlock
            {
                Text = pl.Description,
                Style = (Style)FindResource("T3"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(desc, 0);
            descRow.Children.Add(desc);

            var reload = new Button
            {
                Content = "重载",
                Height = 22,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "只重新加载这个插件",
            };
            reload.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
            var id = pl.Id;
            reload.Click += (_, _) =>
            {
                reload.IsEnabled = false;
                reload.Content = "…";
                try { ServiceHub.RequestReloadPlugins?.Invoke(id); }
                finally { reload.IsEnabled = true; reload.Content = "重载"; }
            };
            Grid.SetColumn(reload, 1);
            descRow.Children.Add(reload);

            sp.Children.Add(descRow);
        }
        else
        {
            // 没有说明也给出重载入口，避免只能整页重载
            var reload = new Button
            {
                Content = "重载",
                Height = 22,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "只重新加载这个插件",
            };
            reload.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
            var id0 = pl.Id;
            reload.Click += (_, _) => ServiceHub.RequestReloadPlugins?.Invoke(id0);
            sp.Children.Add(reload);
        }

        if (!string.IsNullOrWhiteSpace(pl.Author))
        {
            sp.Children.Add(new TextBlock
            {
                Text = "作者 " + pl.Author,
                Style = (Style)FindResource("T3"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        if (pl.Permissions.Length > 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text = "权限：" + string.Join(" · ", pl.Permissions),
                Style = (Style)FindResource("T3"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        if (pl.State == PluginState.Failed && !string.IsNullOrWhiteSpace(pl.Error))
        {
            sp.Children.Add(new TextBlock
            {
                Text = "原因：" + pl.Error,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 232, 128, 120)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        // 插件自带设置面板（延迟构建，异常不影响宿主）
        if (pl.Settings is { } sec && pl.IsLoaded)
        {
            try
            {
                var panel = sec.Build();
                if (panel != null)
                {
                    panel.Margin = new Thickness(0, 10, 0, 0);
                    sp.Children.Add(new Border
                    {
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        BorderBrush = new SolidColorBrush(Themes.Outline()),
                        Padding = new Thickness(0, 10, 0, 0),
                        Child = panel,
                    });
                }
            }
            catch (Exception ex)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "设置面板构建失败：" + ex.Message,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(230, 232, 128, 120)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }
        }

        // 失败插件给出日志路径提示
        if (pl.State == PluginState.Failed)
        {
            sp.Children.Add(new TextBlock
            {
                Text = $"日志：data\\plugins\\{pl.Id}\\plugin.log",
                Style = (Style)FindResource("T3"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return new Border { Style = (Style)FindResource("Card"), Width = 590, Child = sp };
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

    /// <summary>
    /// 实时字幕的语音模型卡片：显示当前模型是否已下载 + 手动下载/更新（带进度）。
    ///
    /// 首次点「开始聆听」也会自动下载；这个卡片用于提前下载或在网络恢复后重试。
    /// </summary>
    private Border BuildModelDownloadCard()
    {
        var card = BaseCard("语音模型", "", out var body);

        var dir = LiveModelsDir();
        var store = new Services.AsrModelStore(dir);
        // 按「引擎 + 模型」配置解析这次实际会用的模型（SenseVoice / Whisper）
        var resolved = Services.AsrService.ResolveModelKey(ServiceHub.Config, store);
        var info = Services.AsrModels.Find(resolved);
        var display = info?.DisplayName ?? resolved;
        var loaded = store.Exists(resolved);
        var sizeNote = info == null ? "" : $" · {info.SizeMb} MB · {info.HardwareHint}";
        var status = new TextBlock
        {
            Text = loaded
                ? $"已下载：{display}{sizeNote}\n{dir}"
                : $"未下载：{display}{sizeNote} · 首次开始聆听会自动下载，也可点下面按钮提前下",
            Style = (Style)FindResource("T3"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        body.Children.Add(status);

        var bar = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(bar);

        var btn = new Button
        {
            Content = "下载 / 校验模型",
            Height = 26,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");

        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            bar.Visibility = Visibility.Visible;
            try
            {
                // 统一走 ModelManager：与「翻译 → 模型」子页共享状态，支持取消/续传
                var mgr = ServiceHub.Models;
                if (mgr == null) { status.Text = "模型服务未就绪"; return; }

                if (mgr.AnyDownloading) { mgr.CancelAll(); status.Text = "已取消下载"; return; }

                if (mgr.Exists(resolved))
                {
                    status.Text = $"已下载：{display}（{mgr.SizeBytes(resolved) / 1048576.0:0.0} MB）";
                    bar.Value = 1;
                    return;
                }

                var ok = await mgr.EnsureAsync(resolved, p =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        bar.Value = p.Ratio;
                        status.Text = $"下载中 {p.Ratio:P0}  {p.Text}";
                    }), System.Windows.Threading.DispatcherPriority.Background);
                });
                var st = mgr.StateOf(resolved);
                status.Text = ok && mgr.Exists(resolved)
                    ? $"已下载：{display}（{mgr.SizeBytes(resolved) / 1048576.0:0.0} MB）"
                    : st.Message.Length > 0 ? st.Message : "下载失败";
            }
            catch (Exception ex)
            {
                status.Text = "下载失败：" + ex.Message;
            }
            finally
            {
                bar.Visibility = Visibility.Collapsed;
                btn.IsEnabled = true;
            }
        };
        body.Children.Add(btn);

        return card;
    }

    /// <summary>实时字幕模型目录：data/models（与 LiveService 保持一致）。</summary>
    private static string LiveModelsDir()
    {
        var dir = ServiceHub.Config.Get("live.models_dir", "models");
        if (string.IsNullOrWhiteSpace(dir)) dir = "models";
        if (Path.IsPathRooted(dir)) return dir;
        return Path.Combine(ServiceHub.Config.DataDir, dir);
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

    /// <summary>试听提示音（不受总开关与冷却限制，用于确认系统提示音/自定义 wav 可用）。</summary>
    private Border BuildSoundPreviewCard()
    {
        var card = BaseCard("试听", "播放一次，确认提示音是否正常", out var body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var play = new Button
        {
            Content = "播放",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.FindResource("PillBtn"),
            ToolTip = "试听当前选择的提示音",
        };
        play.Click += (_, _) =>
        {
            ServiceHub.NotifySound.Preview();
            HintText.Text = "已播放提示音；若没听到，请检查系统默认输出设备与音量合成器";
        };
        row.Children.Add(play);
        body.Children.Add(row);
        return card;
    }

    /// <summary>智谱 GLM-4-Flash 免费额度的开通引导（手机号注册即可）。</summary>
    private Border BuildZhipuGuideCard()
    {
        var card = BaseCard("智谱 GLM-4-Flash（免费）怎么开", "", out var body);
        var tb = new TextBlock
        {
            Text = "1) 手机号注册智谱开放平台 open.bigmodel.cn（无需信用卡）\n"
                 + "2) 控制台 → 项目管理 → API Keys → 新建，复制生成的 Key\n"
                 + "3) 把 Key 填到下面「智谱 API Key」，引擎选「智谱 GLM-4-Flash」即可\n"
                 + "免费模型：glm-4-flash（128K，最稳）/ glm-4.7-flash（200K，更新）\n"
                 + "注意：免费模型有并发上限，字幕这种高频场景建议保留回退（默认回退 Google）。",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "AppText3");
        body.Children.Add(tb);
        return card;
    }

    /// <summary>VAD 切句参数说明（静音阈值 / 最长句）。</summary>
    private Border BuildVadTuningGuideCard()
    {
        var card = BaseCard("切句参数怎么调", "", out var body);
        var tb = new TextBlock
        {
            Text = "• 静音切句阈值：说话停顿时长超过它才切句。\n"
                 + "  太小（<300ms）会把句子切碎、字幕碎片化；太大（>1s）字幕出现更晚、长句更少。\n"
                 + "• 最长句时长：一段话最多等多久强制切句。\n"
                 + "  太小（<6s）长句被截断、翻译丢后半句；太大（>15s）字幕延迟明显。\n"
                 + "推荐：静音 450ms / 最长 12 秒（默认）。改动在重启实时字幕后生效。",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "AppText3");
        body.Children.Add(tb);
        return card;
    }

    /// <summary>人声处理说明卡：解释三种后端的区别（VoxLock 只保留一个人声）。</summary>
    private Border BuildEnhanceGuideCard()
    {
        var card = BaseCard("人声处理怎么选", "", out var body);
        var tb = new TextBlock
        {
            Text = "处理链：原始音频 → 人声处理 → VAD 切句 → 语音识别。\n"
                 + "• DPDFNet2：去背景音乐/噪声，保留所有人声（影视/动画推荐，实测强 BGM 下识别明显变好）\n"
                 + "• GTCRN：0.5MB 轻量降噪，效果弱于 DPDFNet，适合低配\n"
                 + "• VoxLock：目标说话人提取，只保留锁定的第一个说话人——多人对话会丢其他角色台词，仅适合单人解说/直播/通话\n"
                 + "模型在「翻译 → 模型 → 人声处理」里下载（DPDFNet 10MB / GTCRN 0.5MB / VoxLock 74MB）。",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "AppText3");
        body.Children.Add(tb);
        return card;
    }

    /// <summary>测试当前引擎链：翻一句日语，显示耗时与结果。</summary>
    private Border BuildTranslateTestCard()
    {
        var card = BaseCard("测试翻译", "用当前引擎（失败会自动回退）翻译一句日语，确认接口可用", out var body);
        var result = new TextBlock
        {
            Text = "",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        result.SetResourceReference(TextBlock.ForegroundProperty, "AppText2");

        var btn = new Button
        {
            Content = "测试",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        btn.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            result.Text = "翻译中…";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var zh = await ServiceHub.Translate.TranslateOnceAsync(
                    "おはようございます。今日もよろしくお願いします。", "ja", "zh-CN");
                sw.Stop();
                result.Text = zh.Length > 0
                    ? $"✓ {sw.ElapsedMilliseconds}ms：{zh}"
                    : $"✗ 没有返回结果（{sw.ElapsedMilliseconds}ms）—— 检查引擎、Key 与网络";
            }
            catch (Exception ex)
            {
                result.Text = "✗ " + ex.Message;
            }
            finally { btn.IsEnabled = true; }
        };

        body.Children.Add(btn);
        body.Children.Add(result);
        return card;
    }

    private void Apply(string key, string value, IConfigService cfg)
    {
        // 按来源的提示音开关：notify.sound.source.<source>（插件来源的键默认不存在）
        if (key.StartsWith("notify.sound.source.", StringComparison.Ordinal))
        {
            cfg.Set(key, value == "true");
            return;
        }

        switch (key)
        {
            case "island.notify_style":
            case "island.notify_plugin_anim":
                cfg.Set(key, value);
                break;

            case "island.notify_duration_ms":
                cfg.Set<int>(key, int.TryParse(value, out var dur) ? dur : 4000);
                break;

            case "island.notify_to_inbox":
                cfg.Set(key, value == "true");
                break;

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

            case "live.vad.silence_ms":
                cfg.Set<int>(key, Math.Clamp(int.TryParse(value, out var silenceMs) ? silenceMs : 450, 150, 1500));
                break;

            case "live.vad.max_segment_ms":
                cfg.Set<int>(key, Math.Clamp(int.TryParse(value, out var segmentMs) ? segmentMs : 12000, 2000, 20000));
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
