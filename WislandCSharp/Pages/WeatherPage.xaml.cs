using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisland.Controls;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland.Pages;

/// <summary>城市条目（名字 + 坐标 + 已取到的天气）。</summary>
public sealed class CityEntry
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public WeatherState? State { get; set; }
    public bool Loading { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>天气页（对应 Python ui/pages/info.py 的 WeatherPage）。</summary>
public partial class WeatherPage : UserControl
{
    private readonly DispatcherTimer _msgClear = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly List<CityEntry> _cities = new();
    private int _selected;

    public WeatherPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            ServiceHub.Weather.Updated -= Weather_Updated;
            _msgClear.Stop();
        };
        _msgClear.Tick += (_, _) => { _msgClear.Stop(); MsgText.Text = ""; };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ServiceHub.Weather.Updated += Weather_Updated;
        EnsureCities();
        SelectCity(Math.Min(_selected, _cities.Count - 1));
    }

    // ---------------- 城市列表 ----------------
    private void EnsureCities()
    {
        if (_cities.Count > 0) return;

        var cfg = ServiceHub.Config;
        var defName = cfg.Get("weather.city", "上海");
        var defLat = cfg.Get("weather.lat", 0.0);
        var defLon = cfg.Get("weather.lon", 0.0);
        var defaultState = ServiceHub.Weather.State;

        _cities.Add(new CityEntry
        {
            Name = string.IsNullOrWhiteSpace(defName) ? "上海" : defName,
            Lat = defLat,
            Lon = defLon,
            State = defaultState.Ok ? defaultState : null,
            IsDefault = true,
        });

        foreach (var item in cfg.Get("weather.cities", new List<object?>()))
        {
            if (item is not Dictionary<string, object?> d) continue;
            var name = d.TryGetValue("name", out var n) ? n as string ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            _cities.Add(new CityEntry
            {
                Name = name,
                Lat = d.TryGetValue("lat", out var la) && la is double lad ? lad : 0,
                Lon = d.TryGetValue("lon", out var lo) && lo is double lod ? lod : 0,
            });
        }

        BuildChips();
    }

    private void BuildChips()
    {
        ChipRow.Children.Clear();
        for (var i = 0; i < _cities.Count; i++)
        {
            var idx = i;
            var city = _cities[i];
            var selected = i == _selected;

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = CompactView.WeatherGlyphChar(city.State?.Icon ?? ""),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(selected ? Themes.PillText() : Themes.Text(0.9)),
            });
            sp.Children.Add(new TextBlock
            {
                Text = city.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
                Foreground = new SolidColorBrush(selected ? Themes.PillText() : Themes.Text(0.75)),
            });
            sp.Children.Add(new TextBlock
            {
                Text = city.Loading ? "…" : (city.State?.Ok == true ? city.State.TempText : "--°"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
                Foreground = new SolidColorBrush(selected ? Themes.PillText(0.8) : Themes.Text2()),
            });

            var chip = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(8, 0, 12, 0),
                Cursor = Cursors.Hand,
                Background = selected
                    ? new SolidColorBrush(Themes.ActivePill())
                    : new SolidColorBrush(Themes.Card()),
                Child = sp,
                ToolTip = city.IsDefault ? $"{city.Name}（默认）右键可设为默认/删除" : $"{city.Name}　右键可设为默认/删除",
            };
            chip.MouseLeftButtonUp += (_, _) => SelectCity(idx);
            chip.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowCityMenu(chip, idx);
            };
            ChipRow.Children.Add(chip);
        }
    }

    /// <summary>右键菜单：设为默认 / 删除该城市。</summary>
    private void ShowCityMenu(FrameworkElement anchor, int index)
    {
        if (index < 0 || index >= _cities.Count) return;

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Themes.Current.MenuBg),
            Foreground = new SolidColorBrush(Themes.Current.MenuFg),
            BorderBrush = new SolidColorBrush(Themes.Current.MenuBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            FontSize = 13,
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        MenuItem Make(string header, bool enabled, Action act)
        {
            var mi = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                Foreground = new SolidColorBrush(Themes.Current.MenuFg),
                Padding = new Thickness(10, 5, 22, 5),
            };
            mi.Click += (_, _) => { act(); };
            return mi;
        }

        var city = _cities[index];
        menu.Items.Add(Make("设为默认", index != 0, () => MakeDefault(index)));
        menu.Items.Add(Make("删除该城市", _cities.Count > 1, () => DeleteCity(index)));
        ServiceHub.PushUiLock?.Invoke("weather-city-menu");
        menu.Closed += (_, _) => ServiceHub.PopUiLock?.Invoke("weather-city-menu");
        menu.IsOpen = true;
    }

    private void MakeDefault(int index)
    {
        if (index <= 0 || index >= _cities.Count) return;
        var city = _cities[index];
        // 移到首位并写回配置
        _cities.RemoveAt(index);
        foreach (var c in _cities) c.IsDefault = false;
        city.IsDefault = true;
        _cities.Insert(0, city);
        _selected = 0;

        ApplyDefaultFromFirst();

        BuildChips();
        SelectCity(0);
        MsgText.Text = $"已设为默认：{city.Name}";
        _msgClear.Start();
    }

    private void DeleteCity(int index)
    {
        if (_cities.Count <= 1) return;
        if (index < 0 || index >= _cities.Count) return;

        var city = _cities[index];
        _cities.RemoveAt(index);
        if (_selected >= _cities.Count) _selected = _cities.Count - 1;
        ApplyDefaultFromFirst();
        BuildChips();
        SelectCity(_selected);
        MsgText.Text = $"已删除：{city.Name}";
        _msgClear.Start();
    }

    /// <summary>把首位城市设为默认并写回配置（删除/调整顺序后保持一致性）。</summary>
    private void ApplyDefaultFromFirst()
    {
        if (_cities.Count == 0) return;
        foreach (var c in _cities) c.IsDefault = false;
        _cities[0].IsDefault = true;

        var cfg = ServiceHub.Config;
        var city = _cities[0];
        cfg.Set("weather.city", city.Name);
        cfg.Set("weather.lat", city.Lat);
        cfg.Set("weather.lon", city.Lon);
        SaveCitiesCfg();
        ServiceHub.Weather.Initialize(city: city.Name, apiKey: cfg.Get("weather.api_key", ""),
            units: cfg.Get("weather.units", "metric"), lang: cfg.Get("weather.lang", "zh_cn"),
            intervalMinutes: cfg.Get("weather.interval_min", 15),
            useOpenMeteo: string.IsNullOrEmpty(cfg.Get("weather.api_key", "")));
    }

    /// <summary>把非默认城市写回 config.json → weather.cities。</summary>
    private void SaveCitiesCfg()
    {
        var list = new List<object?>();
        for (var i = 1; i < _cities.Count; i++)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = _cities[i].Name,
                ["lat"] = _cities[i].Lat,
                ["lon"] = _cities[i].Lon,
            });
        }
        ServiceHub.Config.Set("weather.cities", list);
    }

    // ---------------- 选城 → 取数据 → 渲染 ----------------
    private async void SelectCity(int index)
    {
        if (index < 0 || index >= _cities.Count) return;
        _selected = index;
        var city = _cities[index];
        BuildChips();

        if (city.State is { Ok: true })
        {
            Render(city.State);
            return;
        }

        // 需要拉取
        city.Loading = true;
        BuildChips();
        RenderLoading(city.Name);

        var st = await ServiceHub.Weather.GetWeatherForAsync(city.Lat, city.Lon, city.Name);
        city.Loading = false;
        city.State = st;
        // 若用户已切走，只更新 chip 温度不重绘
        BuildChips();
        if (_selected == index) Render(st);
    }

    private void RenderLoading(string name)
    {
        Body.Visibility = Visibility.Collapsed;
        ErrText.Visibility = Visibility.Visible;
        ErrText.Text = $"正在获取 {name} 的天气…";
    }

    private void Weather_Updated(object? sender, WeatherState st) => Dispatcher.Invoke(() =>
    {
        if (_cities.Count == 0) return;
        if (st.City == _cities[0].Name || string.IsNullOrEmpty(st.City))
        {
            _cities[0].State = st;
            BuildChips();
            if (_selected == 0) Render(st);
        }
    });

    private void Render(WeatherState st)
    {
        if (!st.Ok && st.Temp == 0 && st.Desc.Length == 0)
        {
            Body.Visibility = Visibility.Collapsed;
            ErrText.Visibility = Visibility.Visible;
            ErrText.Text = st.Error.Length > 0 ? st.Error : "正在获取天气…";
            return;
        }
        Body.Visibility = Visibility.Visible;
        ErrText.Visibility = Visibility.Collapsed;

        BigGlyph.Text = CompactView.WeatherGlyphChar(st.Icon);
        BigTemp.Text = st.Ok ? $"{Math.Round(st.Temp)}°" : "--°";
        DescText.Text = st.Desc;
        CityText.Text = st.City;
        MinMaxText.Text = st.Daily.Count > 0
            ? $"最低 {Math.Round(st.Daily[0].Min)}° 最高 {Math.Round(st.Daily[0].Max)}°"
            : "";

        BuildDaily(st);
        BuildFacts(st);
        BuildHourly(st);
    }

    private void BuildDaily(WeatherState st)
    {
        DailyStack.Children.Clear();
        if (st.Daily.Count == 0) return;

        var lo = st.Daily.Min(d => d.Min);
        var hi = st.Daily.Max(d => d.Max);
        var span = Math.Max(1, hi - lo);

        for (var i = 0; i < st.Daily.Count && i < 5; i++)
        {
            var d = st.Daily[i];
            var row = new Grid { Height = 27.6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var day = i == 0 ? "今天" : WeekdayName(d.Date);
            AddCell(row, 0, day, 12.5, FontWeights.Medium, Themes.Text(), HorizontalAlignment.Left);
            AddCell(row, 1, CompactView.WeatherGlyphChar(d.Icon), 16, FontWeights.Normal, Themes.Text(0.9),
                HorizontalAlignment.Center, symbol: true);
            AddCell(row, 2, $"{Math.Round(d.Min)}°", 12, FontWeights.Normal, Themes.Text2(),
                HorizontalAlignment.Right, num: true);

            var bar = new Grid { Margin = new Thickness(0, 9.5, 0, 0), Height = 5, ClipToBounds = true };
            bar.Children.Add(new Border
            { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Themes.Track()) });
            var x0 = (d.Min - lo) / span * 98;
            var w = Math.Max(5, (d.Max - d.Min) / span * 98);
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(x0, 0, 0, 0),
                Width = w,
                Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#5AC8FA"),
                    (Color)ColorConverter.ConvertFromString("#FF9F0A"),
                    new Point(0, 0), new Point(1, 0)),
            });
            Grid.SetColumn(bar, 3);
            row.Children.Add(bar);

            AddCell(row, 4, $"{Math.Round(d.Max)}°", 12, FontWeights.Normal, Themes.Text(),
                HorizontalAlignment.Right, num: true);

            DailyStack.Children.Add(row);
        }
    }

    private static string WeekdayName(string date)
    {
        if (!DateTime.TryParse(date, out var dt)) return "";
        var names = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return names[(int)dt.DayOfWeek];
    }

    private void AddCell(Grid row, int col, string text, double px, FontWeight weight, Color color,
        HorizontalAlignment align, bool num = false, bool symbol = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = px,
            FontWeight = weight,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align,
            FontFamily = new FontFamily(symbol ? "Segoe UI Symbol"
                : num ? Wisland.Theme.Fonts.Num : Wisland.Theme.Fonts.Ui),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(tb, col);
        row.Children.Add(tb);
    }

    private void BuildFacts(WeatherState st)
    {
        FactsGrid.Children.Clear();
        var facts = new (string Key, string Value)[]
        {
            ("体感", st.Ok ? $"{Math.Round(st.FeelsLike)}°" : "--"),
            ("湿度", st.Ok ? $"{st.Humidity}%" : "--"),
            ("风", st.Ok ? $"{WindDir(st.WindDeg)}{st.Wind:0.#}m/s" : "--"),
            ("气压", st.Ok ? $"{st.Pressure}hPa" : "--"),
            ("能见度", st.Visibility > 0 ? $"{st.Visibility / 1000.0:0}km" : "--"),
            ("云量", st.Ok ? $"{st.Clouds}%" : "--"),
            ("日出", Clock(st.Sunrise, st.TzOffset)),
            ("日落", Clock(st.Sunset, st.TzOffset)),
        };
        foreach (var (key, value) in facts)
        {
            var row = new Grid { Height = 22 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = new TextBlock
            {
                Text = key, FontSize = 11, Foreground = new SolidColorBrush(Themes.Text3()),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Ui),
            };
            var v = new TextBlock
            {
                Text = value, FontSize = 12, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Themes.Text()),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
            };
            Grid.SetColumn(k, 0);
            Grid.SetColumn(v, 1);
            row.Children.Add(k);
            row.Children.Add(v);
            FactsGrid.Children.Add(row);
        }
    }

    private static string WindDir(int deg)
    {
        var dirs = new[] { "北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风" };
        var i = (int)(((deg + 22.5) % 360) / 45);
        return dirs[Math.Clamp(i, 0, 7)];
    }

    private static string Clock(long epoch, int tzOffset)
    {
        if (epoch <= 0) return "--";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch).ToOffset(TimeSpan.FromSeconds(tzOffset))
                .ToString("HH:mm");
        }
        catch { return "--"; }
    }

    private void BuildHourly(WeatherState st)
    {
        HourlyGrid.Children.Clear();
        foreach (var h in st.Hourly.Take(8))
        {
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            var time = h.Time > 0
                ? DateTimeOffset.FromUnixTimeSeconds(h.Time).ToOffset(TimeSpan.FromSeconds(st.TzOffset)).ToString("H时")
                : "--";
            col.Children.Add(new TextBlock
            {
                Text = time, FontSize = 11, Foreground = new SolidColorBrush(Themes.Text3()),
                HorizontalAlignment = HorizontalAlignment.Center, Height = 16,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
            });
            col.Children.Add(new TextBlock
            {
                Text = CompactView.WeatherGlyphChar(h.Icon), FontSize = 19,
                Foreground = new SolidColorBrush(Themes.Text(0.9)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4),
                FontFamily = new FontFamily("Segoe UI Symbol"),
            });
            col.Children.Add(new TextBlock
            {
                Text = $"{Math.Round(h.Temp)}°", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Themes.Text()),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
            });
            if (h.Pop >= 20)
            {
                col.Children.Add(new TextBlock
                {
                    Text = $"{h.Pop}%", FontSize = 10,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F9CFF")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontFamily = new FontFamily(Wisland.Theme.Fonts.Num),
                });
            }
            HourlyGrid.Children.Add(col);
        }
    }

    // ---------------- 添加城市 ----------------
    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        BtnAdd.Visibility = Visibility.Collapsed;
        AddBox.Visibility = Visibility.Visible;
        AddBox.Text = "";

        // 输入框需要窗口处于激活状态才能拿到键盘焦点
        try { Window.GetWindow(this)?.Activate(); } catch { }

        // 同一轮里刚设为 Visible 的元素还不能聚焦，等布局完成后再抢焦点
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AddBox.Focus();
            Keyboard.Focus(AddBox);
            AddBox.CaretIndex = AddBox.Text.Length;
        }), DispatcherPriority.Input);
    }

    private async void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelAdd(); return; }
        if (e.Key != Key.Enter) return;

        var q = AddBox.Text.Trim();
        if (q.Length == 0) { CancelAdd(); return; }

        MsgText.Text = $"正在查找 {q}…";
        var results = await ServiceHub.Weather.SearchCityAsync(q);
        if (results is { Count: > 0 })
        {
            var r = results[0];
            _cities.Add(new CityEntry { Name = r.Name, Lat = r.Lat, Lon = r.Lon });
            _selected = _cities.Count - 1;
            SaveCitiesCfg();
            MsgText.Text = $"已添加 {r.Name}";
            BuildChips();
            SelectCity(_selected);
        }
        else
        {
            MsgText.Text = $"未找到 {q}";
        }
        _msgClear.Start();
        CancelAdd();
        // 添加完成后主动释放键盘焦点，否则会残留一个虚线焦点框（分层窗口下尤其明显）
        Keyboard.ClearFocus();
    }

    private void AddBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 不要在这里自动收起输入框：
        // 刚点「+」时输入框刚显示、焦点还没落稳就会触发 LostFocus，
        // 一收起用户就再也无法输入（表现为"输入框锁定"）。
        // 收起交给 Enter / Esc 处理即可。
        if (AddBox.Text.Trim().Length == 0 && Keyboard.FocusedElement == null)
            CancelAdd();
    }

    private void CancelAdd()
    {
        AddBox.Visibility = Visibility.Collapsed;
        BtnAdd.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_selected < 0 || _selected >= _cities.Count) return;
        var city = _cities[_selected];
        city.State = null;
        SelectCity(_selected);
        MsgText.Text = "正在刷新…";
        _msgClear.Start();
    }
}