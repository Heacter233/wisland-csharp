using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// 天气服务：Open-Meteo（免费无 Key，默认）与 OpenWeather（需 Key，自动回退）。
/// 对应 Python weather.py：地理编码 + 当前天气 + hourly(8 格/3h) + daily(5 天) + WMO 映射。
/// </summary>
public class WeatherService : IWeatherService, IDisposable
{
    private const string OmApi = "https://api.open-meteo.com/v1/forecast";
    private const string OmGeo = "https://geocoding-api.open-meteo.com/v1/search";

    private readonly HttpClient _http = CreateHttp();

    /// <summary>
    /// 统一的 HttpClient：带正常 User-Agent，超时 12s。
    /// 不强制 TLS 版本（交给系统协商；强制固定版本在服务器升级后容易失效）。
    /// </summary>
    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("wisland/0.1 (+https://github.com/AsamiAkito/wisland)");
        }
        catch { }
        return http;
    }

    /// <summary>把底层异常转成用户能看懂的中文提示。</summary>
    private static string FriendlyError(Exception ex)
    {
        var msg = ex.GetBaseException().Message ?? "";
        if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("证书", StringComparison.OrdinalIgnoreCase))
            return "网络 TLS 握手失败（系统证书/加密服务异常），天气无法获取。\n可尝试：用浏览器打开 api.open-meteo.com 确认网络，或检查系统时间与根证书。";
        if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
            ex is TaskCanceledException)
            return "获取天气超时，请检查网络连接。";
        if (msg.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("名称", StringComparison.OrdinalIgnoreCase))
            return "域名解析失败，请检查网络或 DNS。";
        return $"天气获取失败：{msg}";
    }
    private readonly Timer _timer;
    private readonly object _lock = new();
    private SynchronizationContext? _ui;
    private WeatherState _state = WeatherState.Empty;

    private int _intervalMinutes = 15;
    private string _city = "上海";
    private string _apiKey = "";
    private string _geoCity = "";
    private double _lat = 31.2304;
    private double _lon = 121.4737;

    public event EventHandler<WeatherState>? Updated;

    public WeatherState State { get { lock (_lock) return _state; } }

    public WeatherService()
    {
        _timer = new Timer(async _ => await PollAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Initialize(string city = "上海", string apiKey = "", string units = "metric",
        string lang = "zh_cn", int intervalMinutes = 15, bool useOpenMeteo = true)
    {
        _ui = SynchronizationContext.Current;
        _apiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(city)) _city = city.Trim();
        _intervalMinutes = Math.Max(1, intervalMinutes);
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(_intervalMinutes));
    }

    public async Task<WeatherState> GetWeatherAsync()
    {
        try { return await FetchAsync(_lat, _lon, _city); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WeatherService] {ex}");
            return new WeatherState { Ok = false, Error = FriendlyError(ex), City = _city, Time = DateTime.Now };
        }
    }

    /// <summary>按指定坐标取天气（多城市切换用，不影响默认城市缓存）。</summary>
    public async Task<WeatherState> GetWeatherForAsync(double lat, double lon, string cityName)
    {
        try { return await FetchAsync(lat, lon, cityName); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WeatherService] for {cityName}: {ex.Message}");
            return new WeatherState { Ok = false, Error = FriendlyError(ex), City = cityName, Time = DateTime.Now };
        }
    }

    private async Task<WeatherState> FetchAsync(double lat, double lon, string cityName)
    {
        if (Math.Abs(lat) < 0.0001 && Math.Abs(lon) < 0.0001)
        {
            // 未指定坐标：走地理编码（默认城市）
            await EnsureGeoAsync();
            lat = _lat;
            lon = _lon;
        }

        var url = $"{OmApi}?latitude={lat:0.####}&longitude={lon:0.####}"
                + "&timezone=auto&timeformat=unixtime&forecast_days=6"
                + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,"
                + "surface_pressure,wind_speed_10m,wind_direction_10m,is_day,visibility,cloud_cover"
                + "&hourly=temperature_2m,weather_code,precipitation_probability,visibility,cloud_cover"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset";

        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Parse(doc.RootElement, cityName);
    }

    private async Task EnsureGeoAsync()
    {
        if (_geoCity == _city) return;
        var results = await SearchCityAsync(_city);
        if (results is { Count: > 0 })
        {
            _lat = results[0].Lat;
            _lon = results[0].Lon;
            _geoCity = _city;
        }
    }

    public async Task<List<WeatherGeoResult>?> SearchCityAsync(string cityName)
    {
        try
        {
            // Open-Meteo 的地名库里，地级市常以「朝阳市」这种带后缀的形式存在，
            // 直接搜「朝阳」只会命中同名的区/镇（还有外国的同名点），
            // 于是用户回车后就加到了别的地方。这里多查一次带后缀的名字再合并。
            var queries = new List<string> { cityName };
            var raw = cityName.Trim();
            var hasSuffix = raw.EndsWith("市", StringComparison.Ordinal)
                         || raw.EndsWith("区", StringComparison.Ordinal)
                         || raw.EndsWith("县", StringComparison.Ordinal)
                         || raw.EndsWith("镇", StringComparison.Ordinal);
            var isCjk = raw.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            if (!hasSuffix && isCjk) queries.Add(raw + "市");

            var list = new List<WeatherGeoResult>();
            foreach (var q in queries)
            {
                var part = await QueryGeoAsync(q, q == raw ? 15 : 6);
                if (part != null) list.AddRange(part);
            }

            // 注意：这里不能因为 list 为空就提前 return —— 那会让 Nominatim 兜底永远走不到
            // （中文区县/县级市在 Open-Meteo 里就是 0 条）。

            // 去重（同一坐标只留一条）
            var deduped = new List<WeatherGeoResult>();
            foreach (var item in list)
            {
                if (deduped.Exists(d => Math.Abs(d.Lat - item.Lat) < 0.01 && Math.Abs(d.Lon - item.Lon) < 0.01))
                    continue;
                deduped.Add(item);
            }

            // 按 行政级别 + 人口 + 本国 排序：地级市/省会排在同名小村庄前面
            deduped.Sort((a, b) => Score(b, isCjk).CompareTo(Score(a, isCjk)));
            if (deduped.Count > 0) return deduped;

            // Open-Meteo 的地名库对中文区县/县级市覆盖很差（昆山市、常熟市等直接 0 条），
            // 这时用 OSM Nominatim 兜底：它收录了中文行政区名。
            return await QueryNominatimAsync(cityName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WeatherService] SearchCity: {ex.Message}");
            return null;
        }
    }

    // ---------------- Nominatim（OSM）中文地名兜底 ----------------

    private const string NominatimApi = "https://nominatim.openstreetmap.org/search";
    private static DateTime _lastNominatim = DateTime.MinValue;
    private static readonly SemaphoreSlim _nominatimLock = new(1, 1);

    /// <summary>
    /// 用 OpenStreetMap Nominatim 查地名。
    /// 使用条款要求：可识别 UA、每秒最多 1 次请求 —— 这里用信号量 + 间隔保证，
    /// 且只在用户「添加城市」时触发，不参与日常轮询。
    /// </summary>
    private async Task<List<WeatherGeoResult>?> QueryNominatimAsync(string name)
    {
        await _nominatimLock.WaitAsync();
        try
        {
            var wait = 1100 - (DateTime.UtcNow - _lastNominatim).TotalMilliseconds;
            if (wait > 0) await Task.Delay((int)wait);

            var url = $"{NominatimApi}?q={Uri.EscapeDataString(name)}"
                    + "&format=jsonv2&limit=8&accept-language=zh-CN&addressdetails=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd("Wisland/0.2 (Windows desktop widget; +https://github.com/Heacter233/wisland-csharp)");
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");

            using var resp = await _http.SendAsync(req);
            _lastNominatim = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var list = new List<WeatherGeoResult>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("lat", out var latEl) || !e.TryGetProperty("lon", out var lonEl)) continue;
                if (!double.TryParse(latEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
                if (!double.TryParse(lonEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;

                var address = e.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a : default;

                string Addr(string key)
                {
                    if (address.ValueKind != JsonValueKind.Object) return "";
                    return address.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                }

                // 名称优先级：市 > 县/区 > 镇 > 村；display_name 首段作为最终兜底
                var city = Addr("city");
                var county = Addr("county");
                var town = Addr("town");
                var municipality = Addr("municipality");
                var display = e.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? dn.GetString() ?? "" : "";
                var firstSeg = display.Split(',')[0].Trim();

                var name2 = !string.IsNullOrWhiteSpace(municipality) ? municipality
                          : !string.IsNullOrWhiteSpace(city) ? city
                          : !string.IsNullOrWhiteSpace(county) ? county
                          : !string.IsNullOrWhiteSpace(town) ? town
                          : firstSeg;

                // 省份：state（省/自治区）优先，没有再退回 city
                var state = Addr("state");
                var admin2 = !string.IsNullOrWhiteSpace(county) && county != name2 ? county : "";

                list.Add(new WeatherGeoResult
                {
                    Name = name2,
                    Lat = lat,
                    Lon = lon,
                    State = state,
                    Admin2 = admin2,
                    Country = Addr("country"),
                    CountryCode = Addr("country_code").ToUpperInvariant(),
                    Key = e.TryGetProperty("place_id", out var pid) && pid.ValueKind == JsonValueKind.Number
                        ? pid.GetInt64().ToString() : "",
                    // Nominatim 不给人口/地物代码，用 0/空；顺序本身就是相关性排序
                    Population = 0,
                    FeatureCode = "",
                });
            }

            if (list.Count == 0) return list;

            // 去重（同名同坐标只留一条）
            var deduped = new List<WeatherGeoResult>();
            foreach (var item in list)
            {
                if (deduped.Exists(d => Math.Abs(d.Lat - item.Lat) < 0.01 && Math.Abs(d.Lon - item.Lon) < 0.01))
                    continue;
                deduped.Add(item);
            }
            return deduped;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WeatherService] Nominatim({name}): {ex.Message}");
            return null;
        }
        finally
        {
            _nominatimLock.Release();
        }
    }

    private async Task<List<WeatherGeoResult>?> QueryGeoAsync(string name, int count)
    {
        try
        {
            var url = $"{OmGeo}?name={Uri.EscapeDataString(name)}&count={count}&language=zh&format=json";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            var list = new List<WeatherGeoResult>();
            if (doc.RootElement.TryGetProperty("results", out var results))
                foreach (var r in results.EnumerateArray())
                {
                    list.Add(new WeatherGeoResult
                    {
                        Name = Str(r, "name"),
                        Lat = Dbl(r, "latitude"),
                        Lon = Dbl(r, "longitude"),
                        Country = Str(r, "country"),
                        CountryCode = Str(r, "country_code"),
                        State = Str(r, "admin1"),
                        Admin2 = Str(r, "admin2"),
                        Population = r.TryGetProperty("population", out var pop) && pop.ValueKind == JsonValueKind.Number
                            ? (long)pop.GetDouble() : 0,
                        Key = r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                            ? id.GetInt64().ToString() : "",
                        FeatureCode = Str(r, "feature_code"),
                    });
                }
            return list;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WeatherService] QueryGeo({name}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 排序评分：本国 + 行政级别 + 人口规模。
    ///
    /// 只按人口排会被同名小镇挤掉真正的地级市，所以给 feature_code 加权：
    /// 首都 &gt; 一级行政区驻地 &gt; 二级 &gt; 三级 &gt; 普通城市。
    /// </summary>
    private static double Score(WeatherGeoResult r, bool preferCn)
    {
        var score = Math.Log10(Math.Max(1, r.Population));

        // geonames feature_code：PPLC 首都 / PPLA 一级驻地 / PPLA2、PPLA3 下级
        var fc = r.FeatureCode.ToUpperInvariant();
        score += fc switch
        {
            "PPLC" => 4.0,
            "PPLA" => 3.2,
            "PPLA2" => 2.4,
            "PPLA3" => 1.8,
            "PPLA4" => 1.2,
            "PPL" => 0.8,
            _ => 0.0,
        };

        if (preferCn && string.Equals(r.CountryCode, "CN", StringComparison.OrdinalIgnoreCase))
            score += 2.0;
        return score;
    }

    private WeatherState Parse(JsonElement root, string cityName)
    {
        var hourly = new List<HourlyWeather>();
        var daily = new List<DailyWeather>();
        var st = new WeatherState { Ok = true, City = cityName, Time = DateTime.Now, Hourly = hourly, Daily = daily };

        if (root.TryGetProperty("current", out var cur))
        {
            var isDay = cur.TryGetProperty("is_day", out var iday) && iday.GetInt32() == 1;
            var code = cur.TryGetProperty("weather_code", out var wc) ? wc.GetInt32() : 0;
            st = new WeatherState
            {
                Ok = true, City = cityName, Time = DateTime.Now, Hourly = hourly, Daily = daily,
                Temp = Dbl(cur, "temperature_2m"),
                FeelsLike = Dbl(cur, "apparent_temperature"),
                Desc = GetWeatherDescription(code),
                Icon = GetWeatherIcon(code, isDay).icon,
                Humidity = cur.TryGetProperty("relative_humidity_2m", out var h) ? h.GetInt32() : 0,
                Wind = Dbl(cur, "wind_speed_10m"),
                WindDeg = cur.TryGetProperty("wind_direction_10m", out var wd) ? wd.GetInt32() : 0,
                Pressure = cur.TryGetProperty("surface_pressure", out var p) ? (int)Math.Round(p.GetDouble()) : 0,
                Visibility = cur.TryGetProperty("visibility", out var vis) ? (int)Math.Round(vis.GetDouble()) : 0,
                Clouds = cur.TryGetProperty("cloud_cover", out var cl) ? cl.GetInt32() : 0,
            };
        }

        // hourly：从当前整点起，3 小时一格，共 8 格
        if (root.TryGetProperty("hourly", out var hr) && hr.TryGetProperty("time", out var ht))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var start = 0;
            for (var i = 0; i < ht.GetArrayLength(); i++)
            {
                if (ht[i].GetInt64() >= now) { start = i; break; }
                start = i;
            }
            var temps = hr.GetProperty("temperature_2m");
            var codes = hr.GetProperty("weather_code");
            var hasPop = hr.TryGetProperty("precipitation_probability", out var pp);
            var hasVis = hr.TryGetProperty("visibility", out var visArr);
            var hasCloud = hr.TryGetProperty("cloud_cover", out var cloudArr);

            // current 里没有能见度/云量，用当前整点的 hourly 值补齐
            if (hasVis && visArr[start].ValueKind == JsonValueKind.Number)
                st = st with { Visibility = (int)Math.Round(visArr[start].GetDouble()) };
            if (hasCloud && cloudArr[start].ValueKind == JsonValueKind.Number)
                st = st with { Clouds = cloudArr[start].GetInt32() };

            for (var i = start; i < ht.GetArrayLength() && hourly.Count < 8; i += 3)
            {
                var code = codes[i].GetInt32();
                hourly.Add(new HourlyWeather
                {
                    Time = ht[i].GetInt64(),
                    Temp = temps[i].GetDouble(),
                    Icon = GetWeatherIcon(code, true).icon,
                    Pop = hasPop && pp[i].ValueKind != JsonValueKind.Null ? pp[i].GetInt32() : 0,
                    Visibility = hasVis && visArr[i].ValueKind == JsonValueKind.Number
                        ? (int)Math.Round(visArr[i].GetDouble()) : 0,
                    Clouds = hasCloud && cloudArr[i].ValueKind == JsonValueKind.Number
                        ? cloudArr[i].GetInt32() : 0,
                });
            }
        }

        // daily：最多 5 天 + 日出日落
        // 注意：timeformat=unixtime 时 daily.time 是**时间戳**（当天 00:00 的当地时刻）。
        // 直接用 DateTimeOffset.FromUnixTimeSeconds 会按 UTC 解释，导致整列日期早一天
        // （例如今天周五却把明天的周六显示成"周五"）。必须先加上响应里的 utc_offset_seconds。
        var tzOffset = root.TryGetProperty("utc_offset_seconds", out var tzEl) && tzEl.ValueKind == JsonValueKind.Number
            ? tzEl.GetInt32() : 0;

        if (root.TryGetProperty("daily", out var da) && da.TryGetProperty("time", out var dt))
        {
            var n = Math.Min(5, dt.GetArrayLength());
            for (var i = 0; i < n; i++)
            {
                var tEl = dt[i];
                var dateStr = tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? ""
                    : DateTimeOffset.FromUnixTimeSeconds(tEl.GetInt64() + tzOffset)
                        .UtcDateTime.ToString("yyyy-MM-dd");
                daily.Add(new DailyWeather
                {
                    Date = dateStr,
                    Min = da.GetProperty("temperature_2m_min")[i].GetDouble(),
                    Max = da.GetProperty("temperature_2m_max")[i].GetDouble(),
                    Icon = GetWeatherIcon(da.GetProperty("weather_code")[i].GetInt32(), true).icon,
                });
            }
            if (da.TryGetProperty("sunrise", out var sr) && sr.GetArrayLength() > 0)
            {
                var sunrise = sr[0].GetInt64();
                var sunset = da.GetProperty("sunset")[0].GetInt64();
                st = new WeatherState
                {
                    Ok = st.Ok, Error = st.Error, City = st.City, Temp = st.Temp, FeelsLike = st.FeelsLike,
                    Desc = st.Desc, Icon = st.Icon, Humidity = st.Humidity, Wind = st.Wind,
                    WindDeg = st.WindDeg, Pressure = st.Pressure, Time = st.Time,
                    Visibility = st.Visibility, Clouds = st.Clouds,
                    Hourly = hourly, Daily = daily,
                    Sunrise = sunrise, Sunset = sunset,
                    TzOffset = tzOffset,
                };
            }
        }
        return st;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static double Dbl(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;

    private async Task PollAsync()
    {
        try
        {
            var st = await GetWeatherAsync();
            lock (_lock) _state = st;
            if (_ui != null) _ui.Post(_ => Updated?.Invoke(this, st), null);
            else Updated?.Invoke(this, st);
        }
        catch (Exception ex) { Debug.WriteLine($"[WeatherService] Poll: {ex.Message}"); }
    }

    public (string icon, bool isNight) GetWeatherIcon(int wmoCode, bool isDay)
    {
        var name = wmoCode switch
        {
            0 => "clear", 1 => "mainly_clear", 2 => "partly_cloudy", 3 => "overcast",
            45 or 48 => "fog",
            51 or 53 or 55 or 56 or 57 => "drizzle",
            61 => "rain_light", 63 => "rain", 65 => "rain_heavy", 66 or 67 => "freezing_rain",
            71 => "snow_light", 73 => "snow", 75 => "snow_heavy", 77 => "snow_grains",
            80 or 81 or 82 => "showers", 85 or 86 => "snow_showers",
            95 => "thunder", 96 or 99 => "thunder_hail",
            _ => "clear",
        };
        return (isDay ? name : name + "_night", !isDay);
    }

    public string GetWeatherDescription(int code) => code switch
    {
        0 => "晴", 1 => "大部晴", 2 => "局部多云", 3 => "阴",
        45 => "雾", 48 => "雾凇",
        51 => "毛毛雨", 53 => "毛毛雨", 55 => "浓毛毛雨", 56 => "冻毛毛雨", 57 => "冻毛毛雨",
        61 => "小雨", 63 => "中雨", 65 => "大雨", 66 => "冻雨", 67 => "冻雨",
        71 => "小雪", 73 => "中雪", 75 => "大雪", 77 => "雪粒",
        80 => "阵雨", 81 => "阵雨", 82 => "强阵雨",
        85 => "阵雪", 86 => "阵雪",
        95 => "雷阵雨", 96 => "雷阵雨伴冰雹", 99 => "雷阵雨伴冰雹",
        _ => "未知",
    };

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}