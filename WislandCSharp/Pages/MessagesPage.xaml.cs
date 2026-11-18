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

    public MessagesPage()
    {
        InitializeComponent();
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

    private static Color LevelColor(string level) => level switch
    {
        "warn" => Color.FromRgb(0xF0, 0xB4, 0x29),
        "error" => Color.FromRgb(0xE8, 0x4D, 0x5A),
        _ => Color.FromRgb(0x5A, 0x9B, 0xF0),
    };

    public void Refresh()
    {
        BuildTiles();

        // 合并：系统通知 + 插件推送（按时间倒序，最多 30 条）
        var rows = new List<MsgRow>();
        foreach (var h in Services.ServiceHub.Notify.History)
            rows.Add(new MsgRow(h.Name, h.Description, h.Time, KindColors.Get(h.Kind), h.Kind, null));
        foreach (var m in Services.ServiceHub.Inbox.Items)
        {
            var desc = string.IsNullOrWhiteSpace(m.Body) ? m.Title
                     : string.IsNullOrWhiteSpace(m.Title) ? m.Body
                     : $"{m.Title} · {m.Body}";
            rows.Add(new MsgRow(m.DisplaySource, desc, m.Time, LevelColor(m.Level), m.Source, m));
        }

        HistoryList.Children.Clear();
        foreach (var r in rows.OrderByDescending(r => r.Time).Take(30))
        {
            HistoryList.Children.Add(BuildRow(r));
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

        var notify = Services.ServiceHub.Notify;
        foreach (var (kind, name, glyph, color) in BuiltInTiles)
        {
            var count = notify.Counts.GetValueOrDefault(kind);
            var latest = notify.History.FirstOrDefault(h => h.Kind == kind);
            TilesPanel.Children.Add(BuildTile(kind, name, glyph, color, count,
                latest != null ? latest.Time.ToString("HH:mm") : "", name));
        }

        // 插件来源：按来源聚合成磁贴（未读数 + 最近时间）
        var inbox = Services.ServiceHub.Inbox;
        foreach (var g in inbox.Items.GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase))
        {
            var latest = g.OrderByDescending(m => m.Time).First();
            var unread = g.Count(m => !m.Read);
            var color = LevelColor(latest.Level);
            TilesPanel.Children.Add(BuildTile(
                "plugin:" + g.Key, latest.DisplaySource, PluginGlyph, color, unread,
                latest.Time.ToString("HH:mm"), latest.DisplaySource));
        }
    }

    private Border BuildTile(string tag, string name, string glyph, Color color,
        int count, string time, string tooltip)
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
        return tile;
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;

        if (tag.StartsWith("plugin:", StringComparison.Ordinal))
        {
            // 点插件磁贴 = 该来源标记已读
            Services.ServiceHub.Inbox.MarkRead(tag["plugin:".Length..]);
            return;
        }

        Services.ServiceHub.Notify.Open(tag);
    }

    // ---------------- 列表行 ----------------

    private UIElement BuildRow(MsgRow item)
    {
        var grid = new Grid { Height = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(item.Dot),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

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
        try { Services.ServiceHub.Inbox.Clear(); } catch { }
        try { Services.ServiceHub.Notify.ClearAll(); } catch { }
        Refresh();
    }
}