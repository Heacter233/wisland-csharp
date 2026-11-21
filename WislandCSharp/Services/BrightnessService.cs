using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace Wisland.Services;

/// <summary>
/// 亮度服务：WMI（root\wmi：WmiMonitorBrightness / WmiMonitorBrightnessMethods）读写内置屏亮度。
/// 对应 Python services/audio.py 的 BrightnessService（简化：仅 WMI，不做 DDC/CI）。
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    /// <summary>亮度变化（0..100 列表，每个显示器一项）。</summary>
    public event EventHandler<IReadOnlyList<int>>? Changed;

    public bool Available { get; private set; }
    public IReadOnlyList<int> Levels { get; private set; } = Array.Empty<int>();

    public BrightnessService()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, 0, 3000);
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            var list = new List<int>();
            foreach (ManagementObject mo in searcher.Get())
            {
                list.Add(Convert.ToInt32(mo["CurrentBrightness"] ?? 0));
                mo.Dispose();
            }

            var changed = !Available || !Levels.SequenceEqual(list);
            Available = list.Count > 0;
            Levels = list;
            // 亮度没变就不抛事件：原来每 3 秒无条件通知，控制页会跟着白重建一次滑块
            if (changed) Changed?.Invoke(this, list);
        }
        catch
        {
            Available = false;
        }
    }

    /// <summary>设置亮度（0..100）。index 为 null 时应用到所有显示器。</summary>
    public void Set(int? index, int value)
    {
        try
        {
            value = Math.Clamp(value, 0, 100);
            using var searcher = new ManagementObjectSearcher(
                "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in searcher.Get())
            {
                mo.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)value });
                mo.Dispose();
            }
            Poll();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrightnessService] Set: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}