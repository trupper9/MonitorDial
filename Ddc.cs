using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorDial;

/// <summary>
/// Controle de monitor por DDC/CI usando somente a API nativa do Windows
/// (Monitor Configuration API, dxva2.dll). Nenhuma biblioteca de terceiros.
/// </summary>
public sealed class DdcMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, out uint type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] byte[] caps, uint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    private IntPtr _handle;
    private readonly int _index;   // posição na enumeração, usada para reabrir o monitor
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextAllowedMs;
    private bool _disposed;

    public string Description { get; }

    /// <summary>Identificador PnP do monitor, como "GSM76F9" (fabricante + produto).</summary>
    public string PnpId { get; }

    /// <summary>Nome gravado no EDID do monitor, como "LG ULTRAWIDE". Vazio se não houver.</summary>
    public string EdidName { get; }

    /// <summary>GSM é o código de fabricante da LG (Goldstar).</summary>
    public bool IsLg => PnpId.StartsWith("GSM", StringComparison.OrdinalIgnoreCase);

    private DdcMonitor(IntPtr handle, int index, string description, string pnpId, string edidName)
    {
        _handle = handle;
        _index = index;
        Description = description;
        PnpId = pnpId;
        EdidName = edidName;
    }

    private static List<(IntPtr handle, string description, string pnpId, string edidName)> EnumerateRaw()
    {
        var result = new List<(IntPtr, string, string, string)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint n) || n == 0) return true;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
            for (uint i = 0; i < arr.Length; i++)
            {
                var (pnpId, edidName) = ReadIdentity(hMon, i);
                result.Add((arr[i].hPhysicalMonitor, arr[i].szPhysicalMonitorDescription, pnpId, edidName));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;

    // Com EDD_GET_DEVICE_INTERFACE_NAME o DeviceID vem como
    // "\\?\DISPLAY#GSM76F9#7&3afa14d2&1&UID268#{guid}": fabricante+produto e instância.
    private static (string pnpId, string edidName) ReadIdentity(IntPtr hMon, uint index)
    {
        try
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMon, ref info)) return ("", "");
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(info.szDevice, index, ref device, EDD_GET_DEVICE_INTERFACE_NAME)) return ("", "");
            var parts = (device.DeviceID ?? "").Split('#');
            if (parts.Length < 3) return ("", "");
            return (parts[1], ReadEdidName(parts[1], parts[2]));
        }
        catch
        {
            return ("", "");
        }
    }

    // O EDID fica no registro; o descritor de tipo 0xFC guarda o nome do monitor.
    private static string ReadEdidName(string pnpId, string instance)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pnpId}\{instance}\Device Parameters");
        if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return "";
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != 0xFC) continue;
            return Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n')[0].Trim();
        }
        return "";
    }
    /// <summary>Lista os monitores físicos que respondem a DDC/CI.</summary>
    public static List<DdcMonitor> Enumerate()
    {
        var result = new List<DdcMonitor>();
        var raw = EnumerateRaw();
        for (int i = 0; i < raw.Count; i++)
        {
            var m = new DdcMonitor(raw[i].handle, i, raw[i].description, raw[i].pnpId, raw[i].edidName);
            if (m.TryGet(0x10, out _, out _)) result.Add(m);   // responde a brilho => fala DDC/CI
            else m.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Reabre o monitor. O Windows invalida o identificador quando o vídeo é
    /// reconfigurado (por exemplo, ao ligar ou desligar o FreeSync).
    /// </summary>
    private void Reopen()
    {
        var raw = EnumerateRaw();
        for (int i = 0; i < raw.Count; i++)
        {
            // O identificador antigo não é destruído aqui: se o Windows já o invalidou,
            // destruí-lo de novo corrompe a memória do processo.
            if (i == _index) _handle = raw[i].handle;
            else DestroyPhysicalMonitor(raw[i].handle);
        }
    }

    // O monitor precisa de um intervalo entre comandos; sem isso as leituras falham.
    private void WaitTurn()
    {
        long wait = _nextAllowedMs - _clock.ElapsedMilliseconds;
        if (wait > 0) Thread.Sleep((int)wait);
    }

    public bool TryGet(byte code, out int current, out int max)
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                WaitTurn();
                bool ok = GetVCPFeatureAndVCPFeatureReply(_handle, code, out _, out uint cur, out uint mx);
                _nextAllowedMs = _clock.ElapsedMilliseconds + 60;
                if (ok) { current = (int)cur; max = (int)mx; return true; }
                _nextAllowedMs = _clock.ElapsedMilliseconds + 250;
                if (attempt >= 1 && !_disposed) Reopen();
            }
            current = -1; max = -1;
            return false;
        }
    }

    public bool TrySet(byte code, int value)
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                WaitTurn();
                bool ok = SetVCPFeature(_handle, code, (uint)value);
                _nextAllowedMs = _clock.ElapsedMilliseconds + 150;
                if (ok) return true;
                _nextAllowedMs = _clock.ElapsedMilliseconds + 300;
                if (!_disposed) Reopen();
            }
            return false;
        }
    }

    public string GetCapabilities()
    {
        lock (_lock)
        {
            WaitTurn();
            try
            {
                if (!GetCapabilitiesStringLength(_handle, out uint len) || len == 0) return "";
                // A resposta é texto ASCII terminado em zero; o buffer tem folga de sobra.
                var buffer = new byte[len + 16];
                if (!CapabilitiesRequestAndCapabilitiesReply(_handle, buffer, len)) return "";
                int end = Array.IndexOf(buffer, (byte)0);
                return Encoding.ASCII.GetString(buffer, 0, end < 0 ? (int)len : end);
            }
            finally { _nextAllowedMs = _clock.ElapsedMilliseconds + 100; }
        }
    }

    public Task<(bool ok, int current, int max)> GetAsync(byte code) =>
        Task.Run(() => { bool ok = TryGet(code, out int c, out int m); return (ok, c, m); });

    public Task<bool> SetAsync(byte code, int value) => Task.Run(() => TrySet(code, value));

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DestroyPhysicalMonitor(_handle);
        }
    }
}
