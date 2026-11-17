using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>剪贴板页（对应 Python ui/pages/tools.py 的 ClipboardPage）。</summary>
public partial class ClipboardPage : UserControl
{
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private int _flashId = -1;

    public ClipboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ServiceHub.Clipboard.Changed += Clipboard_Changed;
            _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flashId = -1; Refresh(); };
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            ServiceHub.Clipboard.Changed -= Clipboard_Changed;
            _flashTimer.Stop();
        };
    }

    private void Clipboard_Changed(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);

    public void Refresh()
    {
        ClipGrid.Children.Clear();
        ClipGrid.RowDefinitions.Clear();
        ClipScroll.Refresh();
        var items = ServiceHub.Clipboard.History;

        ClearBtn.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = items.Count > 0 ? $"{items.Count} 条记录" : "";

        if (items.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            Height = 150;
            ServiceHub.RequestRetargetHeight?.Invoke();
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        Height = 400;
        ServiceHub.RequestRetargetHeight?.Invoke();

        ClipScroll.Refresh();
        for (var i = 0; i < items.Count; i++)
        {
            var row = i / 2;
            if (ClipGrid.RowDefinitions.Count <= row)
                ClipGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = BuildCard(items[i]);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, (i % 2) * 2);
            ClipGrid.Children.Add(card);
        }
    }

    /// <summary>按显示尺寸解码，避免大图占内存。</summary>
    private static ImageSource? LoadThumb(string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 460;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private UIElement BuildCard(ClipboardEntry item)
    {
        var flashing = item.Id == _flashId;
        var card = new Border
        {
            Width = 252,
            Height = 212,
            Margin = new Thickness(0, 0, 0, 20),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12),
            Cursor = Cursors.Hand,
            Background = flashing
                ? new SolidColorBrush(Themes.ActivePill())
                : new SolidColorBrush(Themes.Card(0.75)),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        if (flashing)
        {
            var copied = new TextBlock
            {
                Text = "已复制",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Themes.PillText()),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            };
            grid.Children.Add(copied);
        }
        else
        {
            if (item.Kind == "image" && item.ImagePath is { } path && System.IO.File.Exists(path))
            {
                // 图片条目：直接显示缩略图，右下角叠尺寸
                var host = new Grid { ClipToBounds = true };
                var img = new Image
                {
                    Source = LoadThumb(path),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                host.Children.Add(img);
                if (item.Text is { Length: > 0 } size)
                {
                    var badge = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(5, 1, 5, 1),
                        Margin = new Thickness(0, 0, 0, 2),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                        Child = new TextBlock
                        {
                            Text = size,
                            FontSize = 10,
                            Foreground = Brushes.White,
                            FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
                        },
                    };
                    host.Children.Add(badge);
                }
                Grid.SetRow(host, 0);
                grid.Children.Add(host);
            }
            else
            {
                var text = new TextBlock
                {
                    Text = item.Text ?? "",
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Themes.Text()),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetRow(text, 0);
                grid.Children.Add(text);
            }
        }

        // 底栏：时间 + 图钉 + 删除
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

        var time = new TextBlock
        {
            Text = item.Time.ToString("HH:mm"),
            FontSize = 10.5,
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
            Foreground = new SolidColorBrush(Themes.Text3()),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 0);
        bar.Children.Add(time);

        var pin = new Button
        {
            Content = item.Pinned ? "\uE718" : "\uE77A",
            Style = (Style)FindResource("IconBtn"),
            Width = 24,
            Height = 24,
            FontSize = 11,
            Opacity = item.Pinned ? 1.0 : 0.45,
            ToolTip = item.Pinned ? "取消置顶" : "置顶",
        };
        pin.Click += (_, _) => ServiceHub.Clipboard.TogglePin(item.Id);
        Grid.SetColumn(pin, 1);
        bar.Children.Add(pin);

        var del = new Button
        {
            Content = "\uE711",
            Style = (Style)FindResource("IconBtn"),
            Width = 24,
            Height = 24,
            FontSize = 11,
            Opacity = 0.45,
            ToolTip = "删除",
        };
        del.Click += (_, _) => ServiceHub.Clipboard.Remove(item.Id);
        Grid.SetColumn(del, 2);
        bar.Children.Add(del);

        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        card.Child = grid;
        card.MouseLeftButtonUp += (_, _) =>
        {
            ServiceHub.Clipboard.CopyEntry(item);
            _flashId = item.Id;
            Refresh();
            _flashTimer.Start();
        };
        return card;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ServiceHub.Clipboard.ClearHistory();
}