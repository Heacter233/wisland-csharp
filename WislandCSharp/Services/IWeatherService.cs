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