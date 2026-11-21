using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>翻译页（对应 Python ui/pages/tools.py 的 TranslatePage）。</summary>
public partial class TranslatePage : UserControl
{
    private readonly List<(string Sid, string Jp, string Zh, bool Final)> _rows = new();
    private string _partialJp = "";
    private int _req;
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private double _levelTarget;

    public TranslatePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
        _levelTimer.Tick += (_, _) => AnimateLevel();

        // 只在构造时订阅一次：Loaded 会随页面反复显示/隐藏多次触发，
        // 在 OnLoaded 里 += 会让每次进页都多挂一份闭包（内存与重复执行双累积）。
        InputBox.TextChanged += (_, _) =>
        {
            InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
        RefreshListenButton();
        RefreshSubs();
    }

    private void Attach()
    {
        ServiceHub.Translate.Delta += OnDelta;
        ServiceHub.Translate.Finished += OnFinished;
        ServiceHub.Translate.Failed += OnFailed;
        ServiceHub.Live.Partial += OnPartial;
        ServiceHub.Live.Segment += OnSegment;
        ServiceHub.Live.LiveZh += OnLiveZh;
        ServiceHub.Live.FinalZh += OnFinalZh;
        ServiceHub.Live.Level += OnLevel;
        ServiceHub.Live.Status += OnStatus;
        ServiceHub.Live.Error += OnError;
        ServiceHub.Live.RunningChanged += OnRunning;
        ServiceHub.Live.DownloadingChanged += OnDownloading;
        if (ServiceHub.Models != null) ServiceHub.Models.Changed += OnModelChanged;
        if (ServiceHub.Wordbook != null) ServiceHub.Wordbook.Changed += OnWordbookChanged;
    }

    private void Detach()
    {
        ServiceHub.Translate.Delta -= OnDelta;
        ServiceHub.Translate.Finished -= OnFinished;
        ServiceHub.Translate.Failed -= OnFailed;
        ServiceHub.Live.Partial -= OnPartial;
        ServiceHub.Live.Segment -= OnSegment;
        ServiceHub.Live.LiveZh -= OnLiveZh;
        ServiceHub.Live.FinalZh -= OnFinalZh;
        ServiceHub.Live.Level -= OnLevel;
        ServiceHub.Live.Status -= OnStatus;
        ServiceHub.Live.Error -= OnError;
        ServiceHub.Live.RunningChanged -= OnRunning;
        ServiceHub.Live.DownloadingChanged -= OnDownloading;
        if (ServiceHub.Models != null) ServiceHub.Models.Changed -= OnModelChanged;
        if (ServiceHub.Wordbook != null) ServiceHub.Wordbook.Changed -= OnWordbookChanged;
        _levelTimer.Stop();
    }

    /// <summary>
    /// 单词本变化（标记掌握 / 删除 / 新增）后刷新列表。
    /// 之前没订阅，点「标记」「删除」后 UI 毫无反应，必须切页再回来才更新。
    /// </summary>
    private void OnWordbookChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsLoaded || PaneWords.Visibility != Visibility.Visible) return;
        RefreshWords();
    }), System.Windows.Threading.DispatcherPriority.Background);

    // ---------------- 分段 ----------------
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        // 先全部收起，再只显示目标分段：避免旧分段内容残留在透明窗口上
        PaneText.Visibility = Visibility.Collapsed;
        PaneLive.Visibility = Visibility.Collapsed;
        PaneWords.Visibility = Visibility.Collapsed;
        ModelsPane.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "0": PaneText.Visibility = Visibility.Visible; break;
            case "1": PaneLive.Visibility = Visibility.Visible; break;
            case "2": PaneWords.Visibility = Visibility.Visible; RefreshWords(); break;
            case "3":
                ModelsPane.Visibility = Visibility.Visible;
                ModelsPane.Refresh();          // 按服务当前状态重建（下载中也能看到实时进度）
                break;
        }

        UpdateLayout();
        // 分段切换会腾出大片透明区域，需要父层重绘，否则旧内容残留在毛玻璃上
        Wisland.Controls.UiRepaint.Ancestors(this);
        Wisland.Controls.UiRepaint.Subtree(this);
    }

    public void ShowLive()
    {
        TabLive.IsChecked = true;
    }

    /// <summary>切到「模型」分段（调试入口 --page:model 与设置页跳转用）。</summary>
    public void ShowModels()
    {
        TabModels.IsChecked = true;
    }

    // ---------------- 文本翻译 ----------------
    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            DoTranslate(InputBox.Text);
        }
    }

    private void BtnTranslate_Click(object sender, RoutedEventArgs e) => DoTranslate(InputBox.Text);

    /// <summary>热键翻译剪贴板文本。</summary>
    public void TranslateClipboardText(string text)
    {
        TabText.IsChecked = true;
        DoTranslate(text);
    }

    private void BtnTranslateClip_Click(object sender, RoutedEventArgs e)
    {
        var text = ServiceHub.Clipboard.GetText() ?? "";
        DoTranslate(text);
    }

    private void DoTranslate(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            StateText.Text = "没有要翻译的文字";
            return;
        }
        InputBox.Text = text;
        OutText.Text = "";
        StateText.Text = "翻译中…";
        _req = ServiceHub.Translate.NextRequestId();
        _ = ServiceHub.Translate.TranslateAsync(text, "ja", "zh-CN", _req);
    }

    private void OnDelta(object? sender, int rid) { }

    private void OnFinished(object? sender, (int requestId, string text) e)
    {
        if (e.requestId != _req) return;
        Dispatcher.Invoke(() =>
        {
            OutText.Text = e.text;
            StateText.Text = "";
        });
    }

    private void OnFailed(object? sender, (int requestId, string error) e)
    {
        if (e.requestId != _req) return;
        Dispatcher.Invoke(() =>
        {
            OutText.Text = $"翻译失败：{e.error}";
            StateText.Text = "";
        });
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (OutText.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(OutText.Text);
            StateText.Text = "已复制";
        }
        catch { }
    }

    // ---------------- 实时字幕 ----------------
    private async void BtnListen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 正在下载模型时按钮变成「取消下载」：真正中断下载（.part 保留可续传）
            if (ServiceHub.Models?.AnyDownloading == true)
            {
                ServiceHub.Models.CancelAll();
                LiveStatus.Text = "已取消模型下载（已下部分保留，可在「模型」里继续）";
                return;
            }

            if (ServiceHub.Live.Running) await ServiceHub.Live.StopAsync();
            else await ServiceHub.Live.StartAsync();
            RefreshListenButton();
        }
        catch (Exception ex)
        {
            LiveStatus.Text = ex.Message;
        }
    }

    private void RefreshListenButton()
    {
        var running = ServiceHub.Live.Running;
        var downloading = ServiceHub.Models?.AnyDownloading == true;
        BtnListen.Content = downloading ? "取消下载" : running ? "停止聆听" : "开始聆听";
        BtnListen.Style = (Style)FindResource(downloading || !running ? "PrimaryBtn" : "PillBtn");
    }

    private void BtnClearSub_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        _partialJp = "";
        RefreshSubs();
    }

    private void OnPartial(object? sender, string jp) => Dispatcher.Invoke(() =>
    {
        _partialJp = jp;
        RefreshSubs();
    });

    private void OnSegment(object? sender, (string sid, string jp) e) => Dispatcher.Invoke(() =>
    {
        _rows.Add((e.sid, e.jp, "", false));
        _partialJp = "";
        RefreshSubs();
    });

    private void OnLiveZh(object? sender, (string sid, string zh) e) => Dispatcher.Invoke(() =>
    {
        UpdateRow(e.sid, e.zh, false);
    });

    private void OnFinalZh(object? sender, (string sid, string zh) e) => Dispatcher.Invoke(() =>
    {
        UpdateRow(e.sid, e.zh, true);
    });

    private void UpdateRow(string sid, string zh, bool final)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Sid != sid) continue;
            _rows[i] = (_rows[i].Sid, _rows[i].Jp, zh, final);
            break;
        }
        RefreshSubs();
    }

    private void OnLevel(object? sender, float v) => Dispatcher.Invoke(() =>
    {
        _levelTarget = Math.Clamp(v * 1.6, 0, 1);
        if (!_levelTimer.IsEnabled) _levelTimer.Start();
    });

    private void AnimateLevel()
    {
        var w = LevelHost.ActualWidth;
        var cur = LevelFill.Width;
        var target = w * _levelTarget;
        var next = cur * 0.4 + target * 0.6;
        LevelFill.Width = Math.Max(0, next);
        if (Math.Abs(next - target) < 0.5 && _levelTarget < 0.02) _levelTimer.Stop();
    }

    private void OnStatus(object? sender, string msg) => Dispatcher.Invoke(() =>
    {
        LiveStatus.Text = msg;
        RefreshListenButton();
    });

    private void OnError(object? sender, string msg) => Dispatcher.Invoke(() => LiveStatus.Text = msg);

    private void OnRunning(object? sender, bool on) => Dispatcher.Invoke(RefreshListenButton);

    private void OnDownloading(object? sender, bool on) => Dispatcher.Invoke(RefreshListenButton);

    /// <summary>模型下载状态变化（可能来自「模型」分段或实时字幕自动下载）。</summary>
    private void OnModelChanged(string model) => Dispatcher.BeginInvoke(new Action(() =>
    {
        RefreshListenButton();
        if (ServiceHub.Models?.AnyDownloading == true)
        {
            var st = ServiceHub.Models.StateOf(model);
            if (st.Running) LiveStatus.Text = $"下载模型 {st.Progress:P0} · {st.Message}";
        }
    }), DispatcherPriority.Background);

    private void RefreshSubs()
    {
        SubStack.Children.Clear();
        var all = _rows.TakeLast(40).ToList();
        if (all.Count == 0 && _partialJp.Length == 0)
        {
            SubStack.Children.Add(new TextBlock
            {
                Text = "播放日语视频后点「开始聆听」",
                Style = (Style)FindResource("T3"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var (_, jp, zh, final) in all)
        {
            SubStack.Children.Add(new TextBlock
            {
                Text = jp,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Themes.Text2()),
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 1),
            });
            SubStack.Children.Add(new TextBlock
            {
                Text = zh.Length > 0 ? zh : "…",
                FontSize = 16,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(final ? Themes.Text() : Themes.Text(0.8)),
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 8),
            });
        }
        if (_partialJp.Length > 0)
        {
            SubStack.Children.Add(new TextBlock
            {
                Text = _partialJp,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Themes.Text3()),
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 8),
            });
        }

        SubScroll.ScrollToEnd();
    }

    // ---------------- 单词本 ----------------
    private void RefreshWords()
    {
        var book = ServiceHub.Wordbook;
        if (book == null) return;

        WordStat.Text = $"共 {book.Total} · 待复习 {book.Learning}";
        WordStack.Children.Clear();

        var items = book.Items.OrderBy(w => w.Known).ThenByDescending(w => w.AddedAt).ToList();
        if (items.Count == 0)
        {
            WordStack.Children.Add(new TextBlock
            {
                Text = "还没有生词。输入日文或英文单词，点「加入」。",
                Style = (Style)FindResource("T3"),
                FontSize = 12,
                Margin = new Thickness(2, 8, 0, 0),
            });
            WordScroll?.Refresh();
            return;
        }

        foreach (var w in items) WordStack.Children.Add(BuildWordRow(w));
        WordScroll?.Refresh();
    }

    private UIElement BuildWordRow(WordEntry w)
    {
        var row = new Grid { Height = 34 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

        var word = new TextBlock
        {
            Text = w.Word,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(w.Known ? Themes.Text3() : Themes.Text()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(word, 0);
        row.Children.Add(word);

        var reading = new TextBlock
        {
            Text = w.Reading,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Themes.Text3()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(reading, 1);
        row.Children.Add(reading);

        var meaning = new TextBlock
        {
            Text = w.Meaning,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Themes.Text2()),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(meaning, 2);
        row.Children.Add(meaning);

        var known = new Button
        {
            Content = w.Known ? "已掌握" : "标记",
            Style = (Style)FindResource("PillBtn"),
            Height = 22,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
        };
        known.Click += (_, _) => ServiceHub.Wordbook?.ToggleKnown(w.Word);
        Grid.SetColumn(known, 3);
        row.Children.Add(known);

        var del = new Button
        {
            Content = "\uE74D",
            Style = (Style)FindResource("IconBtn"),
            Width = 24,
            Height = 24,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "删除",
        };
        del.Click += (_, _) => ServiceHub.Wordbook?.Remove(w.Word);
        Grid.SetColumn(del, 4);
        row.Children.Add(del);

        return row;
    }

    private void BtnAddWord_Click(object sender, RoutedEventArgs e) => AddWordFromInputs();

    /// <summary>输入框里按回车 = 加入单词本。</summary>
    private void WordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddWordFromInputs();
    }

    private void AddWordFromInputs()
    {
        var book = ServiceHub.Wordbook;
        if (book == null) return;

        var word = WordBox.Text.Trim();
        if (word.Length == 0)
        {
            // 没填词：把输入框第一行当作词
            var first = InputBox.Text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
            if (first.Length == 0) return;
            word = first.Length > 40 ? first[..40] : first;
        }

        var ok = book.Add(word, ReadingBox.Text, MeaningBox.Text, "手动");
        if (ok)
        {
            WordBox.Text = "";
            ReadingBox.Text = "";
            MeaningBox.Text = "";
            RefreshWords();          // 立刻刷新列表与统计
        }
    }

    private void BtnReview_Click(object sender, RoutedEventArgs e)
    {
        var book = ServiceHub.Wordbook;
        if (book == null) return;

        var pick = book.PickReview();
        if (pick == null)
        {
            WordStat.Text = "没有待复习的词";
            return;
        }
        // 复习：把词显示到输入框，并提示释义
        TabText.IsChecked = true;
        InputBox.Text = pick.Word;
        WordStat.Text = $"{pick.Word} → {(pick.Meaning.Length > 0 ? pick.Meaning : "（无释义）")}";
    }
}
