using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>消息页（对应 Python ui/pages/home.py 的 MessagesPage）。</summary>
public partial class MessagesPage : UserControl
{
    /// <summary>内置系统通知来源的磁贴定义。</summary>
    private static readonly (string Kind, string Name, string Glyph, Color Color)[] BuiltInTiles =
    {
        ("qq", "QQ", "\uE8F2", Color.FromRgb(0x12, 0xB7, 0xF5)),
        ("wechat", "微信", "\uE8BD", Color.FromRgb(0x07, 0xC1, 0x60)),
        ("mail", "邮件", "\uE715", Color.FromRgb(0xFF, 0x9F, 0x0A)),
    };

    /// <summary>插件来源磁贴用的通用图标。</summary>
    private const string PluginGlyph = "\uE8BD";

    /// <summary>列表默认显示条数（超出折叠）。</summary>
    private const int CollapsedRows = 5;

    /// <summary>列表最多展示条数（全展开时）。</summary>
    private const int MaxRows = 30;

    /// <summary>是否已展开全部消息。</summary>
    private bool _showAll;

    /// <summary>置顶的插件来源（右键磁贴切换，存放在 config 的 island.msg_pinned）。</summary>
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);

    public MessagesPage()
    {
        InitializeComponent();
        LoadPinned();
        Loaded += (_, _) =>
        {
            Services.ServiceHub.Notify.CountsChanged += OnCounts;
            Services.ServiceHub.Notify.Notified += OnNotified;
            Services.ServiceHub.Inbox.Changed += OnInbox;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Services.ServiceHub.Notify.CountsChanged -= OnCounts;
            Services.ServiceHub.Notify.Notified -= OnNotified;
            Services.ServiceHub.Inbox.Changed -= OnInbox;
        };
    }

    private void OnCounts(object? sender, Dictionary<string, int> counts) =>
        Dispatcher.BeginInvoke(new Action(Refresh));

    private void OnNotified(object? sender, Services.NotifyItem item) =>
        Dispatcher.BeginInvoke(new Action(Refresh));

    private void OnInbox(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(Refresh));

    /// <summary>列表里的统一行（系统通知与插件消息合并后共用的结构）。</summary>
    private sealed record MsgRow(
        string Name, string Desc, DateTime Time, Color Dot, string Kind, Services.PluginMessage? Plugin);

    /// <summary>解析 "#RRGGBB" / "#AARRGGBB" / 颜色名；失败返回 null（回落到级别色）。</summary>
    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color c) return c;
        }
        catch { }
        return null;
    }

    /// <summary>系统通知是否镜像进 Inbox（开启时消息页只读 Inbox，避免重复显示）。</summary>
    private static bool NotifyToInbox
    {
        get
        {
            try { return Services.ServiceHub.Config.Get("island.notify_to_inbox", true); }
            catch { return true; }
        }
    }

    private static bool IsBuiltInSource(string source) =>
        source is "qq" or "wechat" or "mail";

    private void LoadPinned()
    {
        _pinned.Clear();
        try
        {
            var raw = Services.ServiceHub.Config.Get("island.msg_pinned", "");
            foreach (var s in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _pinned.Add(s);
        }
        catch { }
    }

    private void SavePinned()
    {
        try
        {
            Services.ServiceHub.Config.Set("island.msg_pinned", string.Join(",", _pinned));
        }
        catch { }
    }

    private static Color LevelColor(string level) => level switch
    {
        "warn" => Color.FromRgb(0xF0, 0xB4, 0x29),
        "error" => Color.FromRgb(0xE8, 0x4D, 0x5A),
        _ => Color.FromRgb(0x5A, 0x9B, 0xF0),
    };

    public void Refresh()
    {
        BuildTiles();

        // 合并：系统通知 + 插件推送（按时间倒序）
        // 开启 island.notify_to_inbox 时系统通知已经镜像进 Inbox，只读 Inbox 即可（否则重复）
        var rows = new List<MsgRow>();
        if (!NotifyToInbox)
        {
            foreach (var h in Services.ServiceHub.Notify.History)
                rows.Add(new MsgRow(h.Name, h.Description, h.Time, KindColors.Get(h.Kind), h.Kind, null));
        }
        foreach (var m in Services.ServiceHub.Inbox.Items)
        {
            var desc = string.IsNullOrWhiteSpace(m.Body) ? m.Title
                     : string.IsNullOrWhiteSpace(m.Title) ? m.Body
                     : $"{m.Title} · {m.Body}";
            rows.Add(new MsgRow(m.DisplaySource, desc, m.Time, LevelColor(m.Level), m.Source, m));
        }

        HistoryList.Children.Clear();
        var ordered = rows.OrderByDescending(r => r.Time).ToList();
        var limit = _showAll ? MaxRows : CollapsedRows;
        foreach (var r in ordered.Take(limit))
        {
            HistoryList.Children.Add(BuildRow(r));
        }
        if (ordered.Count > CollapsedRows)
        {
            HistoryList.Children.Add(BuildMoreRow(ordered.Count));
        }
        if (rows.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = "暂无消息",
                Style = (Style)FindResource("T3"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
            });
        }
    }

    // ---------------- 磁贴 ----------------

    private void BuildTiles()
    {
        TilesPanel.Children.Clear();

        var inbox = Services.ServiceHub.Inbox;
        var groups = inbox.Items
            .GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 内置三块始终显示且顺序固定（QQ → 微信 → 邮件）：
        // 开了 notify_to_inbox 时计数/时间取自 Inbox 里镜像过来的消息，否则取自 NotifyService。
        var notify = Services.ServiceHub.Notify;
        foreach (var (kind, name, glyph, color) in BuiltInTiles)
        {
            var g = groups.FirstOrDefault(x => string.Equals(x.Key, kind, StringComparison.OrdinalIgnoreCase));
            groups.Remove(g!);

            int count;
            string time;
            if (NotifyToInbox)
            {
                count = g?.Count(m => !m.Read) ?? 0;
                var latestAt = g?.Max(m => m.Time);
                time = latestAt is { } t ? t.ToString("HH:mm") : "";
            }
            else
            {
                count = notify.Counts.GetValueOrDefault(kind);
                var latest = notify.History.FirstOrDefault(h => h.Kind == kind);
                time = latest != null ? latest.Time.ToString("HH:mm") : "";
            }

            TilesPanel.Children.Add(BuildTile(kind, name, glyph, color, count, time, name));
        }

        // 其余来源（插件 + 其它）：置顶优先，再按最近消息时间倒序
        groups.Sort((a, b) =>
        {
            var pa = _pinned.Contains(a.Key) ? 0 : 1;
            var pb = _pinned.Contains(b.Key) ? 0 : 1;
            if (pa != pb) return pa - pb;
            return b.Max(m => m.Time).CompareTo(a.Max(m => m.Time));
        });

        foreach (var g in groups)
        {
            var items = g.OrderByDescending(m => m.Time).ToList();
            var latest = items[0];
            var unread = items.Count(m => !m.Read);
            // 图标：取该来源最近一条带图标的消息，否则回落到通用图标
            var glyphItem = items.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(m.IconGlyph) || !string.IsNullOrWhiteSpace(m.IconColor)) ?? latest;
            var glyph = string.IsNullOrWhiteSpace(glyphItem.IconGlyph) ? PluginGlyph : glyphItem.IconGlyph!;
            var color = ParseColor(glyphItem.IconColor) ?? LevelColor(latest.Level);
            TilesPanel.Children.Add(BuildTile(
                "plugin:" + g.Key, latest.DisplaySource, glyph, color, unread,
                latest.Time.ToString("HH:mm"),
                _pinned.Contains(g.Key) ? latest.DisplaySource + "（已置顶，右键取消）"
                                        : latest.DisplaySource + "（右键置顶）",
                pinned: _pinned.Contains(g.Key)));
        }
    }

    private Border BuildTile(string tag, string name, string glyph, Color color,
        int count, string time, string tooltip, bool pinned = false)
    {
        var canvas = new Canvas();

        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(color),
        };
        Canvas.SetLeft(icon, 12);
        Canvas.SetTop(icon, 12);
        icon.Child = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 14,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        canvas.Children.Add(icon);

        var title = new TextBlock
        {
            Text = name,
            Style = (Style)FindResource("T1"),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 88,
        };
        Canvas.SetLeft(title, 50);
        Canvas.SetTop(title, 17);
        canvas.Children.Add(title);

        var num = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            FontFamily = (FontFamily)FindResource("NumFont"),
            Foreground = count > 0
                ? new SolidColorBrush(color)
                : (Brush)FindResource("AppText3"),
        };
        Canvas.SetLeft(num, 14);
        Canvas.SetTop(num, 46);
        canvas.Children.Add(num);

        var timeText = new TextBlock
        {
            Text = time,
            FontSize = 11,
            FontFamily = (FontFamily)FindResource("NumFont"),
            Foreground = (Brush)FindResource("AppText3"),
        };
        Canvas.SetLeft(timeText, 88);
        Canvas.SetTop(timeText, 50);
        canvas.Children.Add(timeText);

        var tile = new Border
        {
            Style = (Style)FindResource("Card"),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Width = 146,
            Height = 72,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = tooltip,
            Tag = tag,
            Child = canvas,
        };
        tile.MouseLeftButtonUp += Tile_Click;

        if (tag.StartsWith("plugin:", StringComparison.Ordinal))
        {
            // 右键：置顶 / 取消置顶
            tile.MouseRightButtonUp += Tile_RightClick;
            if (pinned)
            {
                var pin = new TextBlock
                {
                    Text = "\uE840",     // Pin
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(color),
                };
                Canvas.SetLeft(pin, 130);
                Canvas.SetTop(pin, 6);
                canvas.Children.Add(pin);
            }
        }

        return tile;
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;

        if (tag.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var source = tag["plugin:".Length..];
            // 点磁贴 = 该来源标记已读；内置类型（qq/微信/邮件）还顺带打开对应程序
            Services.ServiceHub.Inbox.MarkRead(source);
            if (IsBuiltInSource(source)) Services.ServiceHub.Notify.Open(source);
            return;
        }

        Services.ServiceHub.Notify.Open(tag);
    }

    /// <summary>右键磁贴：切换置顶（置顶的来源磁贴排在最前）。</summary>
    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (!tag.StartsWith("plugin:", StringComparison.Ordinal)) return;

        e.Handled = true;
        var source = tag["plugin:".Length..];
        if (!_pinned.Remove(source)) _pinned.Add(source);
        SavePinned();
        Refresh();
    }

    /// <summary>列表底部的"显示全部 / 收起"行。</summary>
    private UIElement BuildMoreRow(int total)
    {
        var text = new TextBlock
        {
            Text = _showAll ? "收起" : $"显示全部（共 {total} 条）",
            Style = (Style)FindResource("T3"),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 4),
        };
        var host = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = text,
        };
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _showAll = !_showAll;
            Refresh();
        };
        return host;
    }

    // ---------------- 列表行 ----------------

    private UIElement BuildRow(MsgRow item)
    {
        var grid = new Grid { Height = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        FrameworkElement marker;
        if (item.Plugin is { } pmIcon && !string.IsNullOrWhiteSpace(pmIcon.IconGlyph))
        {
            marker = new TextBlock
            {
                Text = pmIcon.IconGlyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = new SolidColorBrush(ParseColor(pmIcon.IconColor) ?? item.Dot),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            marker = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(item.Dot),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Grid.SetColumn(marker, 0);
        grid.Children.Add(marker);

        var name = new TextBlock
        {
            Text = item.Name,
            Style = (Style)FindResource("T2"),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var desc = new TextBlock
        {
            Text = item.Desc,
            Style = (Style)FindResource("T2"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Desc,
        };
        Grid.SetColumn(desc, 2);
        grid.Children.Add(desc);

        var time = new TextBlock
        {
            Text = item.Time.ToString("HH:mm"),
            Style = (Style)FindResource("T3"),
            FontFamily = (FontFamily)FindResource("NumFont"),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);

        // 插件消息：未读加粗 + 可点击跳转（Uri / Action）
        if (item.Plugin is { } pm)
        {
            if (!pm.Read) name.FontWeight = FontWeights.SemiBold;
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Tag = item,
                Child = grid,
            };
            row.MouseLeftButtonUp += Row_Click;
            return row;
        }

        return grid;
    }

    /// <summary>点击插件消息：标记已读并执行跳转（Uri 优先，其次 Action 回调）。</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not MsgRow row) return;
        if (row.Plugin is not { } pm) return;

        e.Handled = true;
        Services.ServiceHub.Inbox.MarkRead(pm);

        try { pm.Action?.Invoke(); }
        catch (Exception ex) { App.LogCrash("inbox-action", ex); }

        if (!string.IsNullOrWhiteSpace(pm.Uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(pm.Uri!) { UseShellExecute = true });
            }
            catch (Exception ex) { App.LogCrash("inbox-open-uri", ex); }
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        // 「全部已读」= 标记为已读（保留历史），并清掉系统通知的未读计数
        try { Services.ServiceHub.Inbox.MarkRead(); } catch { }
        try { Services.ServiceHub.Notify.ClearAll(); } catch { }
        Refresh();
    }
}