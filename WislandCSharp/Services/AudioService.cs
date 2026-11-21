using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wisland.Services;

/// <summary>
/// 系统音量/静音/峰值电平服务（CoreAudio COM：IAudioEndpointVolume + IAudioMeterInformation）。
/// 对应 Python services/audio.py 的 AudioService。
/// </summary>
public sealed class AudioService : IDisposable
{
    private IAudioEndpointVolume? _volume;
    private IAudioMeterInformation? _meter;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private float _lastVolume = -1;
    private bool _lastMuted;
    private bool _disposed;

    /// <summary>峰值变化阈值：低于它的抖动不值得唤醒订阅方重绘（约 0.4% 满幅）。</summary>
    private const double PeakEpsilon = 0.004;

    /// <summary>(volume 0..1, muted)</summary>
    public event EventHandler<(double Volume, bool Muted)>? VolumeChanged;

    /// <summary>峰值电平 0..1。</summary>
    public event EventHandler<double>? PeakChanged;

    public double Peak { get; private set; }
    public double Volume { get; private set; }
    public bool Muted { get; private set; }
    public bool Available { get; private set; }

    public AudioService()
    {
        TryInit();
        _timer = new Timer(_ => Poll(), null, 250, 250);
    }

    private void TryInit()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device);
            if (hr != 0 || device == null) return;

            var iidVol = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iidVol, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var volObj) == 0)
                _volume = volObj as IAudioEndpointVolume;

            var iidMeter = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iidMeter, 23, IntPtr.Zero, out var meterObj) == 0)
                _meter = meterObj as IAudioMeterInformation;

            Available = _volume != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] init: {ex.Message}");
            Available = false;
        }
        finally
        {
            // 枚举器与设备对象用完即释放：原实现只 Release 了 _volume/_meter，
            // 这两个 COM 对象每 250ms 泄漏一次（切默认设备后还会持续失败）。
            if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
            if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
        }
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            if (_volume != null)
            {
                _volume.GetMasterVolumeLevelScalar(out var v);
                _volume.GetMute(out var mute);
                var muted = mute != 0;
                bool changed;
                lock (_lock)
                {
                    changed = Math.Abs(v - _lastVolume) > 0.005 || muted != _lastMuted || _lastVolume < 0;
                    _lastVolume = v;
                    _lastMuted = muted;
                }
                Volume = v;
                Muted = muted;
                if (changed) VolumeChanged?.Invoke(this, (v, muted));
            }
            if (_meter != null)
            {
                // 只在"有订阅者 + 电平确有变化"时才抛事件：
                // 原来每 250ms 无条件触发（哪怕没人听、电平一直是 0），
                // 订阅方（收起态小形象 / 主页波形）每次都会请求重绘。
                _meter.GetPeakValue(out var peak);
                var level = Math.Clamp((double)peak, 0, 1);
                var changed = Math.Abs(level - Peak) > PeakEpsilon;
                Peak = level;
                if (changed && PeakChanged != null) PeakChanged.Invoke(this, level);
            }
        }
        catch { }
    }

    public void SetVolume(double v)
    {
        try
        {
            v = Math.Clamp(v, 0, 1);
            _volume?.SetMasterVolumeLevelScalar((float)v, Guid.Empty);
            Volume = v;
        }
        catch { }
    }

    public void SetMuted(bool muted)
    {
        try
        {
            _volume?.SetMute(muted ? 1 : 0, Guid.Empty);
            Muted = muted;
        }
        catch { }
    }

    public void ToggleMute() => SetMuted(!Muted);

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        if (_volume != null) Marshal.ReleaseComObject(_volume);
        if (_meter != null) Marshal.ReleaseComObject(_meter);
    }

    // ---------------- COM 定义 ----------------
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        int GetDevice(string id, out IMMDevice? device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetChannelVolumeLevel(uint channel, float level, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        int SetMute(int mute, Guid eventContext);
        int GetMute(out int mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(Guid eventContext);
        int VolumeStepDown(Guid eventContext);
        int QueryHardwareSupport(out uint mask);
        int GetVolumeRange(out float min, out float max, out float increment);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
        int GetMeteringChannelCount(out uint count);
        int GetChannelsPeakValues(uint count, [Out] float[] peaks);
        int QueryHardwareSupport(out uint mask);
    }
}