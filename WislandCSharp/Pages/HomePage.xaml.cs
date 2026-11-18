using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>主页（对应 Python ui/pages/home.py 的 HomePage）。</summary>
public partial class HomePage : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private string _coverLoaded = "";
    private Services.MediaState _media = Services.MediaState.Empty;

    public HomePage()
    {
        InitializeComponent();
        LaunchScroll.PreviewMouseWheel += LaunchScroll_PreviewMouseWheel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _tick.Tick += (_, _) =>
        {
            // 两件事互相隔离：任一异常都不能影响另一个
            try { UpdateProgress(); } catch { }
            try { UpdateLyric(); } catch { }
        };
        _anim.Tick += (_, _) => Pet.InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _media = Services.ServiceHub.Media.State;
        Services.ServiceHub.Media.Changed += Media_Changed;

        if (Services.ServiceHub.Audio is { } audio)
        {
            audio.VolumeChanged += Audio_VolumeChanged;
            SetMuted(audio.Muted);
        }

        Services.ServiceHub.Live.RunningChanged += Live_RunningChanged;
        SetLiveUi(Services.ServiceHub.Live.Running);
        Services.ServiceHub.Config.Changed += Config_Changed;

        _tick.Start();
        _anim.Start();
        ApplyMedia(_media);
        BuildLauncher();
        ApplyPinUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Services.ServiceHub.Media.Changed -= Media_Changed;
        if (Services.ServiceHub.Audio is { } audio) audio.VolumeChanged -= Audio_VolumeChanged;
        Services.ServiceHub.Live.RunningChanged -= Live_RunningChanged;
        Services.ServiceHub.Config.Changed -= Config_Changed;
        _tick.Stop();
        _anim.Stop();
    }

    private void Config_Changed(object? sender, EventArgs e) => ApplyPinUi();

    private void ApplyPinUi()
    {
        var pinned = Services.ServiceHub.Config.Get("island.pinned", false);
        ActPinGlyph.Text = pinned ? "\uE718" : "\uE77A";
        ActPinText.Text = pinned ? "常驻展开" : "保持展开";
    }

    // ---------------- 媒体 ----------------
    private void Media_Changed(object? sender, Services.MediaState st)
    {
        Dispatcher.Invoke(() =>
        {
            _media = st;
            ApplyMedia(st);
        });
    }

    private void ApplyMedia(Services.MediaState st)
    {
        MediaTitle.Text = st.Title.Length > 0 ? st.Title : "未在播放";
        BtnPlay.Content = st.Playing ? "\uE769" : "\uE768";
        if (st.Playing) _tick.Start();
        else _tick.Stop();
        UpdateProgress();
        UpdateCover(st);
    }

    private void UpdateCover(Services.MediaState st)
    {
        var path = st.CoverPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (_coverLoaded.Length > 0)
            {
                _coverLoaded = "";
                CoverHost.Background = (Brush)FindResource("AppCard");
                CoverGlyph.Visibility = Visibility.Visible;
            }
            return;
        }
        if (_coverLoaded == path) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            CoverHost.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            CoverGlyph.Visibility = Visibility.Collapsed;
            _coverLoaded = path;
        }
        catch { }
    }

    private void UpdateProgress()
    {
        var dur = _media.Duration;
        var pos = _media.PositionNow;
        var ratio = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
        var host = ProgressFill.Parent as FrameworkElement;
        ProgressFill.Width = Math.Max(0, (host?.ActualWidth ?? 0) * ratio);
        TimeLeft.Text = Fmt(pos);
        TimeRight.Text = Fmt(dur);
    }

    private static string Fmt(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = (int)s;
        return $"{t / 60}:{t % 60:00}";
    }

    private void Progress_Seek(object sender, MouseButtonEventArgs e)
    {
        if (_media.Duration <= 0 || sender is not Grid g) return;
        var ratio = Math.Clamp(e.GetPosition(g).X / Math.Max(1, g.ActualWidth), 0, 1);
        Services.ServiceHub.Media.Seek(_media.Duration * ratio);
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => Services.ServiceHub.Media.SetPrevious();
    private void BtnNext_Click(object sender, RoutedEventArgs e) => Services.ServiceHub.Media.SetNext();
    private void BtnPlay_Click(object sender, RoutedEventArgs e) => Services.ServiceHub.Media.SetPlayPause();
    private void MediaCard_Enter(object sender, MouseEventArgs e) { }
    private void MediaCard_Leave(object sender, MouseEventArgs e) { }

    // ---------------- 音量 / 静音 ----------------
    private void Audio_VolumeChanged(object? sender, (double Volume, bool Muted) e)
        => Dispatcher.Invoke(() => SetMuted(e.Muted));

    private void SetMuted(bool muted)
    {
        ActMuteGlyph.Text = muted ? "\uE74F" : "\uE767";
        ActMuteText.Text = muted ? "已静音" : "静音";
        ActMuteGlyph.Foreground = muted
            ? new SolidColorBrush(Themes.OnColor())
            : (Brush)FindResource("AppText");
    }

    private void ActMute_Click(object sender, MouseButtonEventArgs e) => Services.ServiceHub.Audio?.ToggleMute();

    // ---------------- 实时字幕 ----------------
    private void Live_RunningChanged(object? sender, bool on) => Dispatcher.Invoke(() => SetLiveUi(on));

    private void SetLiveUi(bool on)
    {
        ActLiveText.Text = on ? "聆听中" : "实时字幕";
        ActLiveGlyph.Foreground = on
            ? new SolidColorBrush(Themes.OnColor())
            : (Brush)FindResource("AppText");
    }

    private async void ActLive_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (Services.ServiceHub.Live.Running) await Services.ServiceHub.Live.StopAsync();
            else await Services.ServiceHub.Live.StartAsync();
            if (Services.ServiceHub.Live.Running) Services.ServiceHub.RequestExpand?.Invoke("trans");
        }
        catch { }
    }

    // ---------------- 动作 ----------------
    private void ActPin_Click(object sender, MouseButtonEventArgs e)
    {
        var v = !Services.ServiceHub.Config.Get("island.pinned", false);
        Services.ServiceHub.Config.Set("island.pinned", v);
        ApplyPinUi();
        if (v) Services.ServiceHub.RequestExpand?.Invoke("");
    }

    private void ActPt_Click(object sender, MouseButtonEventArgs e)
    {
        Services.ServiceHub.Config.Set("island.passthrough",
            !Services.ServiceHub.Config.Get("island.passthrough", false));
    }

    // ---------------- 启动器 ----------------
    /// <summary>启动栏：滚轮左右滑动（一行容器内横向滚动）。</summary>
    private void LaunchScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var target = LaunchScroll.HorizontalOffset - e.Delta;
        LaunchScroll.ScrollToHorizontalOffset(Math.Max(0, target));
    }

    private void BuildLauncher()
    {
        LaunchRow.Children.Clear();

        // 置顶优先，其余保持原顺序
        var items = Services.ServiceHub.Config.GetLauncher()
            .Select((it, idx) => (it, idx))
            .OrderByDescending(x => x.it.Pinned)
            .ThenBy(x => x.idx)
            .Select(x => x.it)
            .ToList();

        foreach (var item in items) LaunchRow.Children.Add(BuildLaunchButton(item));
        LaunchRow.Children.Add(BuildAddButton());
    }

    /// <summary>单个启动项按钮：系统图标/自定义图标 + 右键菜单（置顶/自定义图标/移除）。</summary>
    private Button BuildLaunchButton(Services.LauncherItem item)
    {
        var isFolder = item.IsFolder || Directory.Exists(item.Path);
        var name = string.IsNullOrWhiteSpace(item.Name)
            ? Services.ShellIcons.DisplayName(item.Path, isFolder)
            : item.Name;

        var b = new Button
        {
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 2, 0),
            Style = (Style)FindResource("IconBtn"),
            ToolTip = name,                     // 文件夹显示名字而不是路径
        };

        var icon = Services.ShellIcons.Get(item.Path, isFolder, item.Icon);
        if (icon != null)
        {
            b.Content = new Image
            {
                Source = icon,
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
            };
        }
        else
        {
            b.Content = name.Length > 0 ? name[..1] : "?";
            b.FontSize = 15;
            b.FontFamily = (FontFamily)FindResource("UiFont");
        }

        b.Click += (_, _) => Launch(item);

        var menu = new ContextMenu();
        var pin = new MenuItem { Header = item.Pinned ? "取消置顶" : "置顶" };
        pin.Click += (_, _) =>
        {
            item.Pinned = !item.Pinned;
            SaveAndRebuild();
        };
        var iconItem = new MenuItem { Header = "自定义图标…" };
        iconItem.Click += (_, _) => PickCustomIcon(item);
        var remove = new MenuItem { Header = "移除" };
        remove.Click += (_, _) =>
        {
            var list = Services.ServiceHub.Config.GetLauncher();
            list.RemoveAll(x => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            Services.ServiceHub.Config.SaveLauncher(list);
            BuildLauncher();
        };
        menu.Items.Add(pin);
        menu.Items.Add(iconItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        b.ContextMenu = menu;

        return b;
    }

    /// <summary>行尾的 + 号：添加程序/文件或文件夹。</summary>
    private Button BuildAddButton()
    {
        var b = new Button
        {
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 2, 0),
            Style = (Style)FindResource("IconBtn"),
            Content = "\uE710",                 // Add
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 18,
            ToolTip = "添加快捷启动项",
        };

        var menu = new ContextMenu();
        var fileItem = new MenuItem { Header = "添加程序 / 文件…" };
        fileItem.Click += (_, _) => AddByFile();
        var folderItem = new MenuItem { Header = "添加文件夹…" };
        folderItem.Click += (_, _) => AddByFolder();
        menu.Items.Add(fileItem);
        menu.Items.Add(folderItem);
        b.ContextMenu = menu;
        b.Click += (_, _) => { menu.PlacementTarget = b; menu.IsOpen = true; };

        return b;
    }

    private void AddByFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择程序或文件",
            Filter = "程序 (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        AddItem(dlg.FileName, false);
    }

    private void AddByFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件夹" };
        if (dlg.ShowDialog() != true) return;
        AddItem(dlg.FolderName, true);
    }

    private void AddItem(string path, bool isFolder)
    {
        var list = Services.ServiceHub.Config.GetLauncher();
        if (list.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            BuildLauncher();
            return;
        }
        list.Add(new Services.LauncherItem
        {
            Name = Services.ShellIcons.DisplayName(path, isFolder),
            Path = path,
            IsFolder = isFolder,
        });
        Services.ServiceHub.Config.SaveLauncher(list);
        BuildLauncher();
    }

    private void PickCustomIcon(Services.LauncherItem item)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图标（png / ico / jpg）",
            Filter = "图标文件|*.png;*.ico;*.jpg;*.jpeg|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        var list = Services.ServiceHub.Config.GetLauncher();
        var target = list.FirstOrDefault(x =>
            string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;
        target.Icon = dlg.FileName;
        Services.ServiceHub.Config.SaveLauncher(list);
        BuildLauncher();
    }

    private void SaveAndRebuild()
    {
        Services.ServiceHub.Config.SaveLauncher(Services.ServiceHub.Config.GetLauncher());
        BuildLauncher();
    }

    /// <summary>启动：文件夹用资源管理器打开，其余交给 shell。</summary>
    private static void Launch(Services.LauncherItem item)
    {
        try
        {
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path)) return;

            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    $"\"{path}\"") { UseShellExecute = true });
                return;
            }
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                { UseShellExecute = true });
            }
        }
        catch { }
    }

    // ---------------- 歌词 ----------------
    private Services.Lyrics? _lyrics;
    private string _lyricKey = "";
    private bool _lyricBusy;

    /// <summary>每 500ms 刷新：换歌时取歌词，播放时滚动到当前行。</summary>
    private async void UpdateLyric()
    {
        var media = _media;
        if (media.Title.Length == 0)
        {
            LyricBar.Visibility = Visibility.Collapsed;
            return;
        }

        var key = Services.LyricsService.Key(media) ?? "";
        if (key != _lyricKey && !_lyricBusy)
        {
            _lyricKey = key;
            _lyricBusy = true;
            try
            {
                _lyrics = Services.ServiceHub.Lyrics is { } svc ? await svc.GetAsync(media) : null;
            }
            finally { _lyricBusy = false; }
        }

        if (_lyrics == null)
        {
            LyricBar.Visibility = Visibility.Collapsed;
            return;
        }

        var (idx, line, trans) = _lyrics.At(media.PositionNow);

        // 前奏阶段（还没到第一行歌词）显示首行，避免"看起来没有歌词"
        if (idx < 0 && _lyrics.Lines.Count > 0)
        {
            line = _lyrics.Lines[0];
            trans = _lyrics.Trans.Count > 0 ? _lyrics.Trans[0] : "";
        }

        if (line.Length == 0)
        {
            LyricBar.Visibility = Visibility.Collapsed;
            return;
        }

        LyricBar.Visibility = Visibility.Visible;
        if (LyricMain.Text != line) LyricMain.Text = line;
        LyricSub.Text = trans;
        LyricSub.Visibility = trans.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
