using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>控制页（对应 Python ui/pages/system.py 的 ControlsPage）。</summary>
public partial class ControlsPage : UserControl
{
    private bool _suppress;

    public ControlsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (ServiceHub.Audio is { } a) a.VolumeChanged -= Audio_Changed;
            if (ServiceHub.Brightness is { } b) b.Changed -= Brightness_Changed;
            if (ServiceHub.Radio is { } r) r.Changed -= Radio_Changed;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ServiceHub.Audio is { } audio)
        {
            audio.VolumeChanged += Audio_Changed;
            ApplyVolume(audio.Volume, audio.Muted);
        }
        else
        {
            VolText.Text = "不支持";
        }

        if (ServiceHub.Brightness is { } bri && bri.Available)
        {
            bri.Changed += Brightness_Changed;
            ApplyBrightness(bri.Levels);
        }
        else
        {
            BriText.Text = "不支持";
            BriSlider.IsEnabled = false;
            BriSlider.Opacity = 0.45;
        }

        // 无线电（WLAN / 蓝牙）真实状态
        if (ServiceHub.Radio is { } radio)
        {
            radio.Changed += Radio_Changed;
            ApplyRadio(radio.State);
            _ = radio.RefreshAsync();
        }
        else
        {
            WifiSub.Text = "不支持";
            BtSub.Text = "不支持";
        }
        TileWifi.ToolTip = "点击切换 WLAN 开关";
        TileBt.ToolTip = "点击切换蓝牙开关";
    }

    private void Audio_Changed(object? sender, (double Volume, bool Muted) e)
        => Dispatcher.Invoke(() => ApplyVolume(e.Volume, e.Muted));

    private void ApplyVolume(double v, bool muted)
    {
        _suppress = true;
        VolSlider.Value = v;
        _suppress = false;
        VolText.Text = muted ? "静音" : Math.Round(v * 100).ToString();
        VolGlyphBtn.Content = muted ? "\uE74F" : (v < 0.34 ? "\uE995" : "\uE767");
    }

    private void Brightness_Changed(object? sender, System.Collections.Generic.IReadOnlyList<int> levels)
        => Dispatcher.Invoke(() => ApplyBrightness(levels));

    private void ApplyBrightness(System.Collections.Generic.IReadOnlyList<int> levels)
    {
        if (levels.Count == 0)
        {
            BriText.Text = "不支持";
            return;
        }
        var avg = 0;
        foreach (var l in levels) avg += l;
        avg /= levels.Count;
        _suppress = true;
        BriSlider.Value = avg / 100.0;
        _suppress = false;
        BriText.Text = avg.ToString();
    }

    private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || ServiceHub.Audio is not { } audio) return;
        if (e.NewValue > 0.001 && audio.Muted) audio.SetMuted(false);
        audio.SetVolume(e.NewValue);
    }

    private void Bri_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || ServiceHub.Brightness is not { } bri) return;
        bri.Set(null, (int)Math.Round(e.NewValue * 100));
    }

    private void Radio_Changed(object? sender, RadioState st) => Dispatcher.Invoke(() => ApplyRadio(st));

    private void ApplyRadio(RadioState st)
    {
        WifiSub.Text = !st.WifiPresent ? "未检测到"
            : !st.WifiOn ? "已关闭"
            : st.WifiSsid.Length > 0 ? st.WifiSsid : "已开启";
        TileWifi.Opacity = st.WifiPresent ? 1.0 : 0.55;

        BtSub.Text = !st.BtPresent ? "未检测到" : (st.BtOn ? "已开启" : "已关闭");
        TileBt.Opacity = st.BtPresent ? 1.0 : 0.55;

        SetTileOn(TileWifi, st.WifiOn);
        SetTileOn(TileBt, st.BtOn);
    }

    /// <summary>开启状态用强调色描边表示。</summary>
    private void SetTileOn(FrameworkElement tile, bool on)
    {
        if (tile is not Border b) return;
        b.BorderThickness = new Thickness(on ? 1 : 0);
        b.BorderBrush = on ? new SolidColorBrush(Themes.WithAlpha(Themes.Fill(), 0.55)) : Brushes.Transparent;
    }

    private void VolGlyph_Click(object sender, RoutedEventArgs e) => ServiceHub.Audio?.ToggleMute();

    private async void TileWifi_Click(object sender, MouseButtonEventArgs e)
    {
        if (ServiceHub.Radio is not { } radio || !radio.State.WifiPresent)
        {
            OpenSettings("ms-settings:network-wifi");
            return;
        }
        var target = !radio.State.WifiOn;
        if (!await radio.SetWifiAsync(target)) OpenSettings("ms-settings:network-wifi");
        await radio.RefreshAsync();
    }

    private async void TileBt_Click(object sender, MouseButtonEventArgs e)
    {
        if (ServiceHub.Radio is not { } radio || !radio.State.BtPresent)
        {
            OpenSettings("ms-settings:bluetooth");
            return;
        }
        var target = !radio.State.BtOn;
        if (!await radio.SetBluetoothAsync(target)) OpenSettings("ms-settings:bluetooth");
        await radio.RefreshAsync();
    }

    private static void OpenSettings(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { }
    }
}