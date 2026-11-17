using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using RtRadioState = Windows.Devices.Radios.RadioState;

namespace Wisland.Services;

/// <summary>无线电状态（WLAN / 蓝牙）。</summary>
public sealed class RadioState
{
    public bool WifiPresent { get; init; }
    public bool WifiOn { get; init; }
    public string WifiSsid { get; init; } = "";
    public bool BtPresent { get; init; }
    public bool BtOn { get; init; }
}

/// <summary>
/// 无线电服务。
///
/// 开关与状态统一走 WinRT Windows.Devices.Radios（WiFi / Bluetooth 都有 Radio 对象），
/// 当前连接的网络名走 Windows.Networking.Connectivity（WlanConnectionProfileDetails）。
/// 这套 API 与系统"网络和共享中心"一致，避免自己解析 wlanapi 结构体出错。
/// 若 Radios 不可用（权限/驱动），退回 wlanapi 读射频状态。
/// </summary>
public sealed class RadioService : IDisposable
{
    // ---- wlanapi 兜底（只在 Radios 不可用时读取状态）----
    private const int WlanApiVersion = 2;
    private const int OpcodeRadioState = 3;              // wlan_intf_opcode_radio_state
    private const int OpcodeCurrentConnection = 6;       // wlan_intf_opcode_current_connection
    private const int Dot11RadioStateOn = 1;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int version, IntPtr reserved, out int negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode,
        IntPtr reserved, out int size, out IntPtr data, out int opValueType);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr mem);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    public event EventHandler<RadioState>? Changed;

    public RadioState State { get; private set; } = new();

    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => _ = RefreshAsync(), null, 400, 4000);
    }

    public async Task<RadioState> RefreshAsync()
    {
        if (!await _gate.WaitAsync(0)) return State;

        var (wifiPresent, wifiOn) = await QueryWifiRadioAsync();
        if (!wifiPresent)
        {
            // Radios 拿不到 → 用 wlanapi 读射频状态
            var (p, on) = QueryWifiByWlanApi();
            wifiPresent = p;
            wifiOn = on;
        }

        var ssid = QuerySsid();
        var (btPresent, btOn) = await QueryBluetoothAsync();

        var next = new RadioState
        {
            WifiPresent = wifiPresent,
            WifiOn = wifiOn,
            WifiSsid = ssid,
            BtPresent = btPresent,
            BtOn = btOn,
        };

        var changed = next.WifiPresent != State.WifiPresent || next.WifiOn != State.WifiOn ||
                      next.BtPresent != State.BtPresent || next.BtOn != State.BtOn ||
                      next.WifiSsid != State.WifiSsid;
        State = next;
        if (changed) Changed?.Invoke(this, next);

        _gate.Release();
        return next;
    }

    // ---------------- WinRT Radio ----------------
    private static async Task<(bool present, bool on)> QueryWifiRadioAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return (false, false);

            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi)
                    return (true, r.State == RtRadioState.On);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[RadioService] wifi radio: {ex.Message}"); }
        return (false, false);
    }

    private static async Task<(bool present, bool on)> QueryBluetoothAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return (false, false);

            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.Bluetooth)
                    return (true, r.State == RtRadioState.On);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[RadioService] bt: {ex.Message}"); }
        return (false, false);
    }

    /// <summary>当前 WLAN 连接的网络名（未连接返回空）。</summary>
    private static string QuerySsid()
    {
        try
        {
            // 注意：GetInternetConnectionProfile 在有线优先时会返回以太网口，
            // 拿不到 WLAN 详情，所以遍历所有连接配置找 WLAN 那个。
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                var details = profile?.WlanConnectionProfileDetails;
                if (details == null) continue;
                var ssid = details.GetConnectedSsid();
                if (!string.IsNullOrEmpty(ssid)) return ssid;
            }
            return "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioService] ssid: {ex.Message}");
            return "";
        }
    }

    // ---------------- 开关 ----------------
    public async Task<bool> SetWifiAsync(bool on) => await SetRadioAsync(RadioKind.WiFi, on);

    public async Task<bool> SetBluetoothAsync(bool on) => await SetRadioAsync(RadioKind.Bluetooth, on);

    private static async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Debug.WriteLine($"[RadioService] access denied: {access}");
                return false;
            }

            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind != kind) continue;
                var status = await r.SetStateAsync(on ? RtRadioState.On : RtRadioState.Off);
                return status == RadioAccessStatus.Allowed;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[RadioService] SetRadio: {ex.Message}"); }
        return false;
    }

    // ---------------- wlanapi 兜底读取 ----------------
    private static (bool present, bool on) QueryWifiByWlanApi()
    {
        var handle = IntPtr.Zero;
        var list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out handle) != 0) return (false, false);
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero) return (false, false);

            // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems(4) + dwIndex(4) + InterfaceInfo[]
            if (Marshal.ReadInt32(list) <= 0) return (false, false);
            var firstInfo = list + 8;
            var iface = (Guid)Marshal.PtrToStructure(firstInfo, typeof(Guid))!;

            if (WlanQueryInterface(handle, ref iface, OpcodeRadioState, IntPtr.Zero,
                    out _, out var data, out _) != 0 || data == IntPtr.Zero)
                return (true, false);

            try
            {
                // WLAN_RADIO_STATE: dwNumberOfPhys(4) + WLAN_PHY_RADIO_STATE[]
                // WLAN_PHY_RADIO_STATE: dwPhyIndex(4) + dot11SoftwareRadioState(4) + dot11HardwareRadioState(4)
                var nPhys = Marshal.ReadInt32(data);
                if (nPhys <= 0) return (true, false);
                var software = Marshal.ReadInt32(data + 8);
                return (true, software == Dot11RadioStateOn);
            }
            finally { WlanFreeMemory(data); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioService] wlanapi: {ex.Message}");
            return (false, false);
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _gate.Dispose();
    }
}