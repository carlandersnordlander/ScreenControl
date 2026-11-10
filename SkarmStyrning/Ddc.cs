using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SkarmStyrning;

public sealed record InputInfo(uint? Current, List<byte> Supported, bool CapsFromMonitor, string? Error);

/// <summary>Byter input via DDC/CI (MCCS VCP-kod 0x60 "Input Select").</summary>
public static class Ddc
{
    const byte VCP_INPUT = 0x60;

    public static readonly Dictionary<byte, string> StandardInputs = new()
    {
        [0x01] = "VGA 1", [0x02] = "VGA 2", [0x03] = "DVI 1", [0x04] = "DVI 2",
        [0x05] = "Composite 1", [0x06] = "Composite 2", [0x07] = "S-Video 1", [0x08] = "S-Video 2",
        [0x09] = "Tuner 1", [0x0A] = "Tuner 2", [0x0B] = "Tuner 3",
        [0x0C] = "Komponent 1", [0x0D] = "Komponent 2", [0x0E] = "Komponent 3",
        [0x0F] = "DisplayPort 1", [0x10] = "DisplayPort 2", [0x11] = "HDMI 1", [0x12] = "HDMI 2",
        [0x1B] = "USB-C",
    };

    /// <summary>Visas när skärmen inte själv berättar vilka ingångar den har.</summary>
    public static readonly byte[] CommonInputs = { 0x01, 0x03, 0x0F, 0x10, 0x11, 0x12, 0x1B };

    public static string InputName(byte code) =>
        StandardInputs.TryGetValue(code, out var n) ? n : $"Ingång 0x{code:X2}";

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetCapabilitiesStringLength(IntPtr h, out uint length);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, byte[] buffer, uint length);
    #endregion

    static IntPtr FindHmonitor(string gdiName)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi) && string.Equals(mi.szDevice, gdiName, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static readonly object DdcLock = new();

    public sealed record DdcStatus(bool Responded, byte? Input, bool? PowerOn);

    /// <summary>Snabb koll: vilken ingång visas och är skärmen på? (VCP 0x60 och 0xD6)</summary>
    public static DdcStatus ReadStatus(string? gdiName, int index)
    {
        if (gdiName == null) return new(false, null, null);
        try
        {
            return WithMonitor(gdiName, index, h =>
            {
                byte? input = null;
                for (int a = 0; a < 2 && input == null; a++)
                {
                    if (GetVCPFeatureAndVCPFeatureReply(h, VCP_INPUT, out _, out uint c, out _)) input = (byte)(c & 0xFF);
                    else Thread.Sleep(60);
                }
                bool? power = null;
                if (GetVCPFeatureAndVCPFeatureReply(h, 0xD6, out _, out uint p, out _)) power = (p & 0xFF) == 1;
                return new DdcStatus(input != null || power != null, input, power);
            });
        }
        catch { return new(false, null, null); }
    }

    static T WithMonitor<T>(string gdiName, int index, Func<IntPtr, T> work)
    {
        lock (DdcLock) return WithMonitorUnlocked(gdiName, index, work);
    }

    static T WithMonitorUnlocked<T>(string gdiName, int index, Func<IntPtr, T> work)
    {
        var hmon = FindHmonitor(gdiName);
        if (hmon == IntPtr.Zero) throw new InvalidOperationException($"Hittar inte {gdiName}.");
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0)
            throw new InvalidOperationException("Skärmen exponerar inget DDC/CI-gränssnitt.");
        var arr = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, arr))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return work(arr[Math.Min(index, (int)n - 1)].hPhysicalMonitor); }
        finally { DestroyPhysicalMonitors(n, arr); }
    }

    /// <param name="knownSupported">Tidigare inlästa ingångar (capabilities-läsningen är långsam och kan hoppas över).</param>
    public static InputInfo Read(string? gdiName, int index, List<byte>? knownSupported = null)
    {
        if (gdiName == null) return new(null, new(), false, "Skärmen är avstängd i Windows");
        try
        {
            return WithMonitor(gdiName, index, h =>
            {
                var supported = knownSupported;
                bool fromMonitor = knownSupported is { Count: > 0 };
                if (supported == null || supported.Count == 0)
                {
                    string caps = "";
                    for (int a = 0; a < 2 && caps.Length == 0; a++) caps = ReadCaps(h);
                    supported = ParseInputs(caps);
                    fromMonitor = supported.Count > 0;
                }

                uint? cur = null;
                for (int a = 0; a < 3 && cur == null; a++)
                {
                    if (GetVCPFeatureAndVCPFeatureReply(h, VCP_INPUT, out _, out uint c, out _)) cur = c & 0xFF;
                    else Thread.Sleep(60);
                }

                string? err = cur == null && !fromMonitor
                    ? "Svarar inte på DDC/CI (kontrollera att DDC/CI är på i skärmens meny)"
                    : null;
                return new InputInfo(cur, supported, fromMonitor, err);
            });
        }
        catch (Exception ex)
        {
            return new(null, new(), false, ex.Message);
        }
    }

    public static void SetInput(string? gdiName, int index, byte code)
    {
        if (gdiName == null) throw new InvalidOperationException("Skärmen är avstängd i Windows, input kan inte styras.");
        WithMonitor(gdiName, index, h =>
        {
            for (int a = 0; a < 3; a++)
            {
                if (SetVCPFeature(h, VCP_INPUT, code)) return 0;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Skärmen tog inte emot kommandot (DDC/CI avstängt eller ingången stöds inte).");
        });
    }

    static string ReadCaps(IntPtr h)
    {
        if (!GetCapabilitiesStringLength(h, out uint len) || len == 0) return "";
        var buf = new byte[len];
        if (!CapabilitiesRequestAndCapabilitiesReply(h, buf, len)) return "";
        return Encoding.ASCII.GetString(buf).TrimEnd('\0');
    }

    /// <summary>Plockar ut "60( 0F 11 12 )" ur vcp(...)-sektionen i capabilities-strängen.</summary>
    public static List<byte> ParseInputs(string caps)
    {
        var result = new List<byte>();
        int v = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
        if (v < 0) return result;
        var m = Regex.Match(caps[(v + 4)..], @"(?<![0-9A-Fa-f])60\s*\(([0-9A-Fa-f\s]*)\)");
        if (!m.Success) return result;
        foreach (var tok in m.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Vissa skärmar skriver ihop koderna: "0F1112"
            for (int i = 0; i + 1 < tok.Length || i == 0 && tok.Length == 1; i += 2)
            {
                var part = tok.Length == 1 ? tok : tok.Substring(i, 2);
                if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var b) && b != 0 && !result.Contains(b))
                    result.Add(b);
            }
        }
        return result;
    }
}
