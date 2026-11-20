using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Controls;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>
/// 模型管理页：实时字幕的离线语音模型（Whisper GGML）。
///
/// 每张卡片显示：模型名 / 体积 / 状态（已下载或未下载）/ 进度 / 操作按钮
///   - 下载 / 校验：从镜像下载（带进度与魔数校验）
///   - 删除：两步确认，避免误点
///   - 设为默认：写入 live.asr.model（"自动"按 CPU 核心数选择）
/// 如果字幕正在运行，删除当前使用的模型会被拒绝并提示先停止聆听。
/// </summary>
public partial class ModelsPage : UserControl
{
    /// <summary>删除按钮的两步确认状态（模型名 → 是否已进入"确认删除"）。</summary>
    private readonly System.Collections.Generic.HashSet<string> _confirmDelete = new();
    private bool _busy;

    /// <summary>最近一次操作结果的提示（Refresh 重建卡片后会丢失，所以单独记住）。</summary>
    private string _lastMessageModel = "";
    private string _lastMessage = "";

    public ModelsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    // ---------------- 目录 ----------------

    private static string ModelsDir()
    {
        var dir = ServiceHub.Config.Get("live.models_dir", "models");
        if (string.IsNullOrWhiteSpace(dir)) dir = "models";
        if (Path.IsPathRooted(dir)) return dir;
        return Path.Combine(ServiceHub.Config.DataDir, dir);
    }

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ModelsDir();
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private static string ShortPath(string path)
    {
        try
        {
            var parent = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var grand = Path.GetFileName(Path.GetDirectoryName(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "");
            return string.IsNullOrEmpty(grand) ? parent : $"…\\{grand}\\{parent}";
        }
        catch { return path; }
    }

    // ---------------- 构建卡片 ----------------

    private void Refresh()
    {
        var dir = ModelsDir();
        // 标题行空间有限：只显示末两段（…\data\models），完整路径放 ToolTip
        DirText.Text = ShortPath(dir);
        DirText.ToolTip = dir;

        var current = ServiceHub.Config.Get("live.asr.model", "auto");
        var resolved = WhisperAsr.ResolveModel(current);

        CardStack.Children.Clear();
        foreach (var (name, sizeMb, note) in WhisperAsr.Models)
            CardStack.Children.Add(BuildCard(name, sizeMb, note, current, resolved));

        HintText.Text = $"当前使用：{resolved}" +
            (string.Equals(current, "auto", StringComparison.OrdinalIgnoreCase)
                ? $"（由「自动」按 {Environment.ProcessorCount} 核选择）"
                : "（手动指定）") +
            " · 删除模型前请先停止聆听；首次开始聆听也会自动下载。";

        Host.Refresh();
    }

    private Border BuildCard(string name, int sizeMb, string note, string current, string resolved)
    {
        var asr = new WhisperAsr(ModelsDir());
        var exists = asr.Exists(name);
        var isCurrent = string.Equals(name, resolved, StringComparison.OrdinalIgnoreCase);
        var running = ServiceHub.Live?.Running == true;

        var card = new Border
        {
            Width = 592,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Themes.Card()),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧：名称 + 状态 + 进度
        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

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
            FontSize = 11,
            Foreground = new SolidColorBrush(Themes.Text3()),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 状态徽标：已下载 / 未下载 / 正在使用
        if (exists)
        {
            titleRow.Children.Add(Badge(isCurrent ? "使用中" : "已下载",
                isCurrent ? Themes.OnColor() : Themes.Track(),
                isCurrent ? Themes.OnFg() : Themes.Text()));
        }
        else
        {
            titleRow.Children.Add(Badge("未下载", Color.FromArgb(40, 255, 255, 255), Themes.Text2()));
        }
        left.Children.Add(titleRow);

        left.Children.Add(new TextBlock
        {
            Text = note,
            Style = (Style)FindResource("T3"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var status = new TextBlock
        {
            Text = string.Equals(_lastMessageModel, name, StringComparison.OrdinalIgnoreCase) && _lastMessage.Length > 0
                ? _lastMessage
                : exists ? FileSizeText(asr.PathOf(name)) : "点右侧「下载」获取（镜像优先，失败回退官方源）",
            Style = (Style)FindResource("T3"),
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        left.Children.Add(status);

        var bar = new ProgressBar
        {
            Height = 3,
            Margin = new Thickness(0, 7, 0, 0),
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
        };
        left.Children.Add(bar);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // 右侧：操作按钮
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var download = SmallButton(exists ? "校验" : "下载");
        download.Click += async (_, _) => await DownloadAsync(name, exists, status, bar, download);
        actions.Children.Add(download);

        var setDefault = SmallButton("设为默认");
        setDefault.IsEnabled = exists && !isCurrent;
        setDefault.Click += (_, _) =>
        {
            ServiceHub.Config.Set("live.asr.model", name);
            Refresh();
        };
        actions.Children.Add(setDefault);

        var del = SmallButton("删除", danger: true);
        del.IsEnabled = exists && !running;
        del.ToolTip = running ? "字幕正在运行，请先停止聆听" : "删除本地模型文件";
        var confirming = _confirmDelete.Contains(name);
        if (confirming)
        {
            del.Content = "确认删除";
            del.SetResourceReference(Control.BackgroundProperty, "AppUp");
            del.SetResourceReference(Control.ForegroundProperty, "AppOnFg");
        }
        del.Click += (_, _) =>
        {
            if (!_confirmDelete.Contains(name))
            {
                _confirmDelete.Add(name);
                Refresh();
                return;
            }
            _confirmDelete.Remove(name);
            string msg;
            try
            {
                File.Delete(asr.PathOf(name));
                msg = "已删除本地模型文件";
            }
            catch (Exception ex)
            {
                msg = "删除失败：" + ex.Message;
            }
            _lastMessageModel = name; _lastMessage = msg;
            Refresh();
        };
        actions.Children.Add(del);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private async Task DownloadAsync(string name, bool exists, TextBlock status, ProgressBar bar, Button btn)
    {
        if (_busy) return;
        _busy = true;
        btn.IsEnabled = false;
        bar.Visibility = Visibility.Visible;

        try
        {
            var asr = new WhisperAsr(ModelsDir());
            string final;
            if (exists)
            {
                // 已下载：只做一次存在性与大小校验
                final = asr.Exists(name) ? "校验通过：文件完整" : "校验失败：文件缺失或过小，请重新下载";
                status.Text = final;
                bar.Value = 1;
            }
            else
            {
                status.Text = "准备下载…";
                await asr.DownloadAsync(name, (p, msg) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        bar.Value = p;
                        status.Text = $"下载中 {p:P0}  {msg}";
                    }), System.Windows.Threading.DispatcherPriority.Background);
                });
                final = "下载完成";
                status.Text = final;
            }
            _lastMessageModel = name; _lastMessage = final;
        }
        catch (Exception ex)
        {
            status.Text = "下载失败：" + ex.Message;
            _lastMessageModel = name; _lastMessage = status.Text;
        }
        finally
        {
            _busy = false;
            btn.IsEnabled = true;
            bar.Visibility = Visibility.Collapsed;
            // 重新构建卡片，让徽标/按钮状态同步
            Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // ---------------- 小工具 ----------------

    private static Border Badge(string text, Color bg, Color fg) => new()
    {
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(6, 0, 6, 1),
        CornerRadius = new CornerRadius(7),
        Background = new SolidColorBrush(bg),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = new SolidColorBrush(fg),
        },
    };

    private static Button SmallButton(string label, bool danger = false)
    {
        var b = new Button
        {
            Content = label,
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0),
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
        if (danger)
        {
            b.SetResourceReference(Control.BackgroundProperty, "AppCard");
            b.SetResourceReference(Control.ForegroundProperty, "AppText2");
        }
        return b;
    }

    private static string FileSizeText(string path)
    {
        try
        {
            var mb = new FileInfo(path).Length / 1048576.0;
            return $"已下载 · {mb:0.0} MB";
        }
        catch { return "已下载"; }
    }
}
