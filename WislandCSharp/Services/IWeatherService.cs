using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>天气地理编码结果。</summary>
public class WeatherGeoResult
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Country { get; set; } = "";
    public string State { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>国家代码（如 CN / US），用于优先展示本地城市。</summary>
    public string CountryCode { get; set; } = "";

    /// <summary>二级行政区（市/区），用于区分同名地点。</summary>
    public string Admin2 { get; set; } = "";

    /// <summary>geonames 地物代码（PPLC 首都 / PPLA 一级驻地 / PPL 普通城市）。</summary>
    public string FeatureCode { get; set; } = "";

    /// <summary>人口（用于按城市规模排序，避免同名小村庄排在前面）。</summary>
    public long Population { get; set; }

    /// <summary>给用户看的完整描述：城市 · 省 市 · 国家。</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(State)) parts.Add(State);
            if (!string.IsNullOrWhiteSpace(Admin2) && Admin2 != State) parts.Add(Admin2);
            if (!string.IsNullOrWhiteSpace(Country)) parts.Add(Country);
            var s = string.Join(" · ", parts);
            return s;
        }
    }

    /// <summary>坐标文本，便于用户核对（同名城市坐标差别很大）。</summary>
    public string CoordText => $"{Lat:0.00}, {Lon:0.00}";
}

/// <summary>天气服务接口（对应 Python weather.py 的 WeatherService）。</summary>
public interface IWeatherService
{
    WeatherState State { get; }

    event EventHandler<WeatherState>? Updated;

    void Initialize(string city = "上海", string apiKey = "", string units = "metric",
        string lang = "zh_cn", int intervalMinutes = 15, bool useOpenMeteo = true);

    Task<WeatherState> GetWeatherAsync();

    /// <summary>按指定坐标取天气（用于多城市切换）。</summary>
    Task<WeatherState> GetWeatherForAsync(double lat, double lon, string cityName);

    /// <summary>搜索城市（Open-Meteo 地理编码，支持中文）。</summary>
    Task<List<WeatherGeoResult>?> SearchCityAsync(string cityName);

    /// <summary>WMO 天气代码 → 图标名（夜间加 _night 后缀）。</summary>
    (string icon, bool isNight) GetWeatherIcon(int wmoCode, bool isDay);

    /// <summary>WMO 天气代码 → 中文描述。</summary>
    string GetWeatherDescription(int wmoCode);
}