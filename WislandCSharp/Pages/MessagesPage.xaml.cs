using System;
using System.Collections.Generic;
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
    private sealed record MsgRow(string Name, string Desc, DateTime Time, Color Dot);

    private static Color LevelColor(string level) => level switch
    {
        "warn" => Color.FromRgb(0xF0, 0xB4, 0x29),
        "error" => Color.FromRgb(0xE8, 0x4D, 0x5A),
        _ => Color.FromRgb(0x5A, 0x9B, 0xF0),
    };

    public void Refresh()
    {
        var notify = Services.ServiceHub.Notify;
        SetTile(CountQq, TimeQq, "qq", notify.Counts.GetValueOrDefault("qq"));
        SetTile(CountWechat, TimeWechat, "wechat", notify.Counts.GetValueOrDefault("wechat"));
        SetTile(CountMail, TimeMail, "mail", notify.Counts.GetValueOrDefault("mail"));

        // 合并：系统通知 + 插件推送（按时间倒序，最多 30 条）
        var rows = new List<MsgRow>();
        foreach (var h in notify.History)
            rows.Add(new MsgRow(h.Name, h.Description, h.Time, KindColors.Get(h.Kind)));
        foreach (var m in Services.ServiceHub.Inbox.Items)
        {
            var desc = string.IsNullOrWhiteSpace(m.Body) ? m.Title
                     : string.IsNullOrWhiteSpace(m.Title) ? m.Body
                     : $"{m.Title} · {m.Body}";
            rows.Add(new MsgRow(m.DisplaySource, desc, m.Time, LevelColor(m.Level)));
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

    private void SetTile(TextBlock count, TextBlock time, string kind, int n)
    {
        count.Text = n.ToString();
        count.Foreground = n > 0
            ? new SolidColorBrush(KindColors.Get(kind))
            : (Brush)FindResource("AppText3");
        var latest = Services.ServiceHub.Notify.History.FirstOrDefault(h => h.Kind == kind);
        time.Text = latest != null ? latest.Time.ToString("HH:mm") : "";
    }

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
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var desc = new TextBlock
        {
            Text = item.Desc,
            Style = (Style)FindResource("T2"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
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

        return grid;
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string kind)
            Services.ServiceHub.Notify.Open(kind);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        try { Services.ServiceHub.Inbox.Clear(); } catch { }
        Services.ServiceHub.Notify.ClearAll();
    }
}