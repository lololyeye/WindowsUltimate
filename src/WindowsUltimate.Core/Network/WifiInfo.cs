using System.Runtime.InteropServices;

namespace WindowsUltimate.Core.Network;

public sealed record WifiStatus(string Interface, string Ssid, int SignalQuality, int? Rssi, int? Channel, string Band, string Standard, double RxMbps, double TxMbps);

/// <summary>Wi-Fi link details through the native WLAN API (language-independent, no netsh parsing).</summary>
public static class WifiInfo
{
    public static List<WifiStatus> Query()
    {
        var result = new List<WifiStatus>();
        nint handle = 0, list = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out handle) != 0) return result;
            if (WlanEnumInterfaces(handle, 0, out list) != 0) return result;
            var count = Marshal.ReadInt32(list);
            var itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * itemSize);
                if (info.isState != 1) continue; // wlan_interface_state_connected
                var guid = info.InterfaceGuid;

                if (WlanQueryInterface(handle, ref guid, 7, 0, out _, out var connPtr, out _) != 0) continue;
                WLAN_CONNECTION_ATTRIBUTES conn;
                try { conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(connPtr); }
                finally { WlanFreeMemory(connPtr); }

                int? rssi = null, channel = null;
                if (WlanQueryInterface(handle, ref guid, unchecked((int)0x10000102), 0, out _, out var rssiPtr, out _) == 0)
                {
                    rssi = Marshal.ReadInt32(rssiPtr);
                    WlanFreeMemory(rssiPtr);
                }
                if (WlanQueryInterface(handle, ref guid, 8, 0, out _, out var chPtr, out _) == 0)
                {
                    channel = Marshal.ReadInt32(chPtr);
                    WlanFreeMemory(chPtr);
                }

                var a = conn.wlanAssociationAttributes;
                var ssid = System.Text.Encoding.UTF8.GetString(a.dot11Ssid.ucSSID, 0, (int)Math.Min(a.dot11Ssid.uSSIDLength, 32));
                result.Add(new WifiStatus(
                    info.strInterfaceDescription,
                    ssid,
                    (int)a.wlanSignalQuality,
                    rssi,
                    channel,
                    channel is null ? "?" : channel <= 14 ? "2.4 GHz" : "5/6 GHz",
                    a.dot11PhyType switch { 7 => "Wi-Fi 4 (802.11n)", 8 => "Wi-Fi 5 (802.11ac)", 10 => "Wi-Fi 6/6E (802.11ax)", 11 => "Wi-Fi 7 (802.11be)", 4 => "802.11a", 6 => "802.11g", _ => $"PHY {a.dot11PhyType}" },
                    a.ulRxRate / 1000.0,
                    a.ulTxRate / 1000.0));
            }
        }
        catch (DllNotFoundException)
        {
            // WLAN AutoConfig not present (e.g. Server without wireless).
        }
        finally
        {
            if (list != 0) WlanFreeMemory(list);
            if (handle != 0) WlanCloseHandle(handle, 0);
        }
        return result;
    }

    public static string Advice(WifiStatus w)
    {
        if (w.Rssi is < -70 || w.SignalQuality < 50)
            return "Weak signal: expect packet loss and latency spikes in games. Move closer to the router, remove obstacles, or use Ethernet.";
        if (w.Band == "2.4 GHz")
            return "Connected on 2.4 GHz, which is slower and more congested. If your router offers 5 GHz, prefer it for gaming.";
        return "Signal is good.";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        public int bSecurityEnabled;
        public int bOneXEnabled;
        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(nint handle, ref Guid iface, int opcode, nint reserved, out int size, out nint data, out int valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
}
