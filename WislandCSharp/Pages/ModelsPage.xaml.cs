using System;
using System.IO;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisland.Controls;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>
/// 语音模型子页（挂在翻译页「模型」分段下）：离线 ASR 用的 Whisper GGML 模型。
///
/// 每张卡片显示：模型名 / 体积 / 徽标（使用中·已下载·未下载·下载中）/ 状态 / 进度 / 操作
///   - 下载 / 继续 / 重试：断点续传（.part 保留，应用重启也能接上）
///   - 取消：立即中断，已下部分保留
///   - 设为默认：写入 live.asr.model（"自动"按 CPU 核心数选择）
///   - 删除 / 清除缓存：两步确认，避免误点
///
/// 下载状态全部来自 <see cref="ModelManager"/>，本页只负责渲染与转发操作：
/// 因此切到别的分段/页面再回来，进度条会按服务状态重建，而不是"消失"。
/// </summary>
public partial class ModelsPage : UserControl
{
    /// <summary>单张卡片的可变更控件引用（避免每次进度刷新都重建视觉树）。</summary>
    private sealed class CardUi
    {
        public TextBlock Hint = null!;

        public string Name = "";
        public Border Root = null!;
        public Border BadgeHost = null!;
        public TextBlock Status = null!;
        public ProgressBar Bar = null!;
        public Button Primary = null!;
        public Button SetDefault = null!;
        public Button Delete = null!;
        public string BadgeKey = "";
        public string PrimaryKey = "";
        public string DeleteKey = "";
    }

    private readonly Dictionary<string, CardUi> _cards = new();
    private readonly HashSet<string> _confirmDelete = new();
    private bool _hooked;
    private string _model = "auto";

    public ModelsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
    }

    // ---------------- 订阅 ----------------

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        var mgr = ServiceHub.Models;
        if (mgr == null) return;

        if (on)
        {
            mgr.Changed += OnManagerChanged;
            Refresh();
        }
        else
        {
            mgr.Changed -= OnManagerChanged;
        }
    }

    /// <summary>下载进度来自后台线程：切回 UI 线程只更新对应卡片。</summary>
    private void OnManagerChanged(string model) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsLoaded) return;
        if (_cards.TryGetValue(model, out var card)) Apply(card);
        UpdateHint();
    }), DispatcherPriority.Background);

    // ---------------- 目录 ----------------

    private string ModelsDir()
    {
        try { return ServiceHub.Models.Dir; }
        catch { return System.IO.Path.Combine(ServiceHub.Config.DataDir, "models"); }
    }

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ModelsDir();
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private static string ShortPath(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var parent = System.IO.Path.GetFileName(trimmed);
            var grand = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(trimmed) ?? "");
            return string.IsNullOrEmpty(grand) ? parent : $"…\\{grand}\\{parent}";
        }
        catch { return path; }
    }

    // ---------------- 构建 / 刷新 ----------------

    /// <summary>重建卡片（目录、默认模型变化时调用；也供父页切分段时调用）。</summary>
    public void Refresh()
    {
        var dir = ModelsDir();
        DirText.Text = ShortPath(dir);
        DirText.ToolTip = dir;

        _model = ServiceHub.Config.Get("live.asr.model", "auto");

        CardStack.Children.Clear();
        _cards.Clear();
        foreach (var family in new[] { AsrModels.FamilySenseVoice, AsrModels.FamilyWhisper, AsrModels.FamilyEnhance })
        {
            var header = new TextBlock
            {
                Text = AsrModels.FamilyName(family),
                FontSize = 11,
                Margin = new Thickness(2, 10, 0, 6),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "AppText3");
            CardStack.Children.Add(header);

            foreach (var info in AsrModels.Family(family))
            {
                var card = BuildCard(info);
                _cards[info.Key] = card;
                CardStack.Children.Add(card.Root);
            }
        }
        foreach (var card in _cards.Values) Apply(card);

        UpdateHint();
        Host.Refresh();
    }

    /// <summary>当前实际会被使用的模型 key（按引擎配置 + 已下载模型推算）。</summary>
    private string CurrentModelKey()
    {
        try
        {
            var cfg = ServiceHub.Config;
            var store = new AsrModelStore(ModelsDirStatic(cfg));
            return AsrService.ResolveModelKey(cfg, store);
        }
        catch { return WhisperAsr.ResolveModel(_model); }
    }

    private static string ModelsDirStatic(IConfigService cfg)
    {
        var dir = cfg.Get("live.models_dir", "models");
        if (string.IsNullOrWhiteSpace(dir)) dir = "models";
        return Path.IsPathRooted(dir) ? dir : Path.Combine(cfg.DataDir, dir);
    }

    private void UpdateHint()
    {
        var resolved = CurrentModelKey();
        var info = AsrModels.Find(resolved);
        var engine = (ServiceHub.Config.Get("live.asr.engine", "auto") ?? "auto");
        var parts = new List<string>
        {
            $"当前使用：{(info?.DisplayName ?? resolved)}" +
            (string.Equals(_model, "auto", StringComparison.OrdinalIgnoreCase)
                ? $"（引擎 {engine} · 自动按 {Environment.ProcessorCount} 核选模型）"
                : $"（引擎 {engine}）"),
        };

        if (ServiceHub.Models?.AnyDownloading == true) parts.Add("下载中可从卡片取消，已下部分保留");
        else parts.Add("支持断点续传 · 首次开始聆听也会自动下载");

        HintText.Text = string.Join(" · ", parts);
    }

    private CardUi BuildCard(AsrModelInfo info)
    {
        var card = new CardUi { Name = info.Key };
        var name = info.DisplayName;
        var sizeMb = info.SizeMb;
        var note = $"{info.Note}｜语言：{info.Languages}｜{info.HardwareHint}";

        var root = new Border
        {
            Width = 592,
            Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Themes.Card()),
            ToolTip = note,
        };
        card.Root = root;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左：标题行（名称 + 体积 + 徽标）、状态、进度
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Themes.Text()),
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = $"  {sizeMb} MB",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Themes.Text3()),
            VerticalAlignment = VerticalAlignment.Center,
        });

        card.BadgeHost = new Border
        {
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 1),
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { FontSize = 10 },
        };
        titleRow.Children.Add(card.BadgeHost);
        left.Children.Add(titleRow);

        card.Hint = new TextBlock
        {
            Text = $"{info.Note}｜{info.Languages}｜{info.HardwareHint}",
            Style = (Style)FindResource("T3"),
            FontSize = 9.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        left.Children.Add(card.Hint);

        card.Status = new TextBlock
        {
            Style = (Style)FindResource("T3"),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        left.Children.Add(card.Status);

        card.Bar = new ProgressBar
        {
            Height = 3,
            Margin = new Thickness(0, 5, 0, 0),
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
        };
        left.Children.Add(card.Bar);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // 右：操作按钮（内容按状态变化，点击时再判断意图）
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        card.Primary = SmallButton("下载");
        card.Primary.Click += (_, _) => OnPrimaryClick(card);
        actions.Children.Add(card.Primary);

        card.SetDefault = SmallButton("设为默认");
        card.SetDefault.Click += (_, _) =>
        {
            if (!ServiceHub.Models.Exists(card.Name)) return;
            var info = AsrModels.Find(card.Name);
            if (info != null && info.Family == AsrModels.FamilyEnhance)
            {
                ServiceHub.Config.Set("live.enhance.mode", card.Name switch
                {
                    "dpdfnet2" => "dpdfnet",
                    "gtcrn" => "gtcrn",
                    "voxlock" => "voxlock",
                    _ => "off",
                });
            }
            else if (info != null && info.Family == AsrModels.FamilySenseVoice)
            {
                ServiceHub.Config.Set("live.asr.engine", "sensevoice");
            }
            else
            {
                ServiceHub.Config.Set("live.asr.engine", "whisper");
                ServiceHub.Config.Set("live.asr.model", card.Name);
            }
            Refresh();
        };
        actions.Children.Add(card.SetDefault);

        card.Delete = SmallButton("删除");
        card.Delete.Click += (_, _) => OnDeleteClick(card);
        actions.Children.Add(card.Delete);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        root.Child = grid;
        return card;
    }

    // ---------------- 状态渲染 ----------------

    /// <summary>按服务状态刷新一张卡片（不重建视觉树）。</summary>
    private void Apply(CardUi card)
    {
        var mgr = ServiceHub.Models;
        if (mgr == null) return;

        var name = card.Name;
        var st = mgr.StateOf(name);
        var exists = mgr.Exists(name);
        var partial = mgr.PartialBytes(name);
        var info = AsrModels.Find(name);
        var isCurrent = info?.Family == AsrModels.FamilyEnhance
            ? string.Equals(ServiceHub.Config.Get("live.enhance.mode", "dpdfnet"),
                name switch { "dpdfnet2" => "dpdfnet", _ => name },
                StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, CurrentModelKey(), StringComparison.OrdinalIgnoreCase);
        var confirming = _confirmDelete.Contains(name);

        // 徽标
        var (badge, bg, fg) = st.Running
            ? ("下载中", Themes.OnColor(), Themes.OnFg())
            : exists && isCurrent ? ("使用中", Themes.OnColor(), Themes.OnFg())
            : exists ? ("已下载", Themes.Track(), Themes.Text())
            : partial > 0 ? ("已暂停", Color.FromArgb(40, 255, 255, 255), Themes.Text2())
            : ("未下载", Color.FromArgb(40, 255, 255, 255), Themes.Text2());

        if (card.BadgeKey != badge)
        {
            card.BadgeKey = badge;
            card.BadgeHost.Background = new SolidColorBrush(bg);
            ((TextBlock)card.BadgeHost.Child).Text = badge;
            ((TextBlock)card.BadgeHost.Child).Foreground = new SolidColorBrush(fg);
        }

        // 状态文字
        var note = NoteOf(name);
        card.Status.Text = st.Running
            ? $"下载中 {st.Progress:P0} · {st.Message}"
            : exists
                ? $"已下载 {mgr.SizeBytes(name) / 1048576.0:0.0} MB · " +
                  (st.Message.StartsWith("校验", StringComparison.Ordinal) ? st.Message : note)
                : partial > 0
                    ? (st.Canceled || st.Error.Length > 0
                        ? st.Message
                        : $"已缓存 {partial / 1048576.0:0.0} MB · 点「继续」续传")
                    : st.Error.Length > 0 || st.Canceled
                        ? st.Message
                        : $"{note} · 点「下载」获取（镜像优先，失败自动回退官方源）";

        // 进度条（不知道总大小时只更新文字，避免显示一条空槽）
        var totalBytes = st.Total > 0 ? st.Total : mgr.TotalHintBytes(name);
        var showBar = (st.Running || (partial > 0 && !exists)) && totalBytes > 0;
        card.Bar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showBar)
        {
            var ratio = st.Running
                ? st.Progress
                : (double)partial / totalBytes;
            card.Bar.Value = Math.Clamp(ratio, 0, 1);
        }

        // 主按钮：取消 / 继续 / 重试 / 下载 / 校验（下载类操作强调色，取消用中性色）
        var (label, accent) = st.Running
            ? ("取消", false)
            : exists ? ("校验", false)
            : partial > 0 ? ("继续", true)
            : st.Error.Length > 0 || st.Canceled ? ("重试", true)
            : ("下载", true);
        var key = label + "|" + accent;
        if (card.PrimaryKey != key)
        {
            card.PrimaryKey = key;
            card.Primary.Content = label;
            StyleButton(card.Primary, accent);
        }
        card.Primary.IsEnabled = true;
        card.Primary.ToolTip = st.Running ? "取消下载（已下部分保留，可续传）" : null;

        // 设为默认
        card.SetDefault.IsEnabled = exists && !isCurrent && !st.Running;
        card.SetDefault.Content = isCurrent ? "当前默认" : "设为默认";

        // 删除 / 清除缓存
        var canClear = exists || partial > 0;
        var delLabel = confirming
            ? (exists ? "确认删除" : "确认清除")
            : exists ? "删除" : partial > 0 ? "清除缓存" : "删除";
        var delKey = delLabel + "|" + (canClear && !st.Running);
        if (card.DeleteKey != delKey)
        {
            card.DeleteKey = delKey;
            card.Delete.Content = delLabel;
            card.Delete.IsEnabled = canClear && !st.Running;
            StyleButton(card.Delete, confirming);   // 确认态用强调色提醒
        }
        card.Delete.ToolTip = !canClear ? "没有可删除的文件"
            : st.Running ? "下载中，请先取消"
            : exists ? "删除本地模型文件" : "删除未下完的缓存";
    }

    private static string NoteOf(string name) => AsrModels.Find(name)?.Note ?? "";

    // ---------------- 操作 ----------------

    private void OnPrimaryClick(CardUi card)
    {
        var mgr = ServiceHub.Models;
        if (mgr == null) return;
        var st = mgr.StateOf(card.Name);

        if (st.Running) { mgr.Cancel(card.Name); return; }

        if (mgr.Exists(card.Name))
        {
            // 已下载：只做一次本地校验（大小 + 魔数在位）
            var size = mgr.SizeBytes(card.Name);
            st.Error = size > 10 * 1024 * 1024 ? "" : "文件过小，可能不完整";
            st.Canceled = false;
            st.Message = st.Error.Length > 0 ? "校验失败：" + st.Error : "校验通过（文件完整）";
            Apply(card);
            return;
        }

        _ = DownloadAsync(card);
    }

    private async System.Threading.Tasks.Task DownloadAsync(CardUi card)
    {
        var mgr = ServiceHub.Models;
        if (mgr == null) return;
        _confirmDelete.Remove(card.Name);
        Apply(card);
        UpdateHint();

        try
        {
            // 失败/取消的原因已经在状态里（卡片上会显示），这里只需要等它结束
            await mgr.DownloadAsync(card.Name);
        }
        finally
        {
            Apply(card);
            UpdateHint();
        }
    }

    private void OnDeleteClick(CardUi card)
    {
        var mgr = ServiceHub.Models;
        if (mgr == null) return;

        if (!_confirmDelete.Contains(card.Name))
        {
            _confirmDelete.Add(card.Name);
            Apply(card);            // 第一次点：变成"确认删除/确认清除"
            return;
        }

        _confirmDelete.Remove(card.Name);
        if (mgr.Exists(card.Name)) mgr.Delete(card.Name);
        else mgr.ClearPartial(card.Name);
        Apply(card);
        UpdateHint();
    }

    // ---------------- 小工具 ----------------

    private static void StyleButton(Button b, bool accent)
    {
        if (accent)
        {
            b.SetResourceReference(Control.BackgroundProperty, "AppOn");
            b.SetResourceReference(Control.ForegroundProperty, "AppOnFg");
        }
        else
        {
            b.SetResourceReference(Control.BackgroundProperty, "AppCard");
            b.SetResourceReference(Control.ForegroundProperty, "AppText2");
        }
    }

    private static Button SmallButton(string label, bool accent = false)
    {
        var b = new Button
        {
            Content = label,
            Height = 24,
            Padding = new Thickness(9, 0, 9, 0),
            FontSize = 11,
            Margin = new Thickness(5, 0, 0, 0),
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        StyleButton(b, accent);
        return b;
    }
}
